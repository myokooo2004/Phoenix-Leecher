using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using PhoenixLeecher.Models;

namespace PhoenixLeecher.Services;

public sealed class ScanProgress
{
    public int AlbumsFound { get; set; }  // unique albums (deduped entries), not raw links
    public int Target { get; set; } = 50;
    public int PostsDone { get; set; }
    public int TotalPosts { get; set; }
    public bool Running { get; set; }
    public string Note { get; set; } = "";
}

public sealed class ScanState
{
    public string BlogUrl { get; set; } = "";
    public int StartIndex { get; set; } = 1;
    public int AlbumsFound { get; set; }  // albums found in the current run (reset per run)
    public int PostsDone { get; set; }
    public DateTime SavedAt { get; set; }
}

/// <summary>
/// Link discovery engine (user spec 2026-10-15):
/// - Paste URL -> page HTML links auto-found immediately (FindLinksAsync).
/// - Scan -> Blogspot older-posts crawl via Blogger feed API (start-index pagination)
///   until 50 share-links found or posts exhausted.
/// - Dead share-link -> auto retry (HttpRetry) before marking dead.
/// - Stop halts gracefully, state saved; Resume continues from saved feed position,
///   persisted to disk, survives app restart (never re-reads from start).
/// - Dedup: normalized INSIDE-name; folder-vs-folder and file-vs-file only;
///   folder-vs-file never compared (user spec). pCloud preferred, MediaFire kept as backup.
/// </summary>
public sealed class LinkScanner
{
    public const int LinkTarget = 50;
    private static readonly Regex LinkRe = new(
        @"https?://[^\s""'<>]*(?:pcloud\.link|mediafire\.com)[^\s""'<>]*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static string AppDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PhoenixLeecher");
    private static string StatePath => Path.Combine(AppDir, "scanstate.json");

    public static List<string> ExtractLinks(string text) =>
        LinkRe.Matches(text).Select(m => m.Value).Distinct().ToList();

    public static bool IsBlogspot(string url) =>
        url.Contains("blogspot.", StringComparison.OrdinalIgnoreCase);

    public static string NormalizeBlogUrl(string url)
    {
        var u = url.Trim().TrimEnd('/');
        // strip mobile/query variants for state keying
        var q = u.IndexOf('?');
        if (q >= 0) u = u[..q];
        return u.ToLowerInvariant();
    }

    public async Task<List<string>> FindLinksAsync(string pageUrl, CancellationToken ct)
    {
        var html = await HttpRetry.Run(() => HttpRetry.Client.GetStringAsync(pageUrl, ct), ct: ct);
        return ExtractLinks(html);
    }

    public ScanState? LoadState(string blogUrl)
    {
        try
        {
            if (!File.Exists(StatePath)) return null;
            var all = JsonSerializer.Deserialize<Dictionary<string, ScanState>>(File.ReadAllText(StatePath));
            if (all != null && all.TryGetValue(NormalizeBlogUrl(blogUrl), out var s)) return s;
        }
        catch { /* corrupt state -> start fresh */ }
        return null;
    }

    public void SaveState(ScanState state)
    {
        Directory.CreateDirectory(AppDir);
        Dictionary<string, ScanState> all = new();
        try
        {
            if (File.Exists(StatePath))
                all = JsonSerializer.Deserialize<Dictionary<string, ScanState>>(File.ReadAllText(StatePath)) ?? new();
        }
        catch { }
        all[NormalizeBlogUrl(state.BlogUrl)] = state;
        File.WriteAllText(StatePath, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
    }

    public void ClearState(string blogUrl)
    {
        try
        {
            if (!File.Exists(StatePath)) return;
            var all = JsonSerializer.Deserialize<Dictionary<string, ScanState>>(File.ReadAllText(StatePath));
            if (all != null && all.Remove(NormalizeBlogUrl(blogUrl)))
                File.WriteAllText(StatePath, JsonSerializer.Serialize(all));
        }
        catch { }
    }

    /// <summary>
    /// Full scan. Entries dictionary is updated in place (dedup inside).
    /// Returns when target reached, posts exhausted, or cancelled (Stop).
    /// </summary>
    public async Task ScanAsync(
        string blogUrl,
        ConcurrentDictionary<string, LeecherEntry> entries,
        IProgress<ScanProgress> progress,
        CancellationToken ct)
    {
        var state = LoadState(blogUrl) ?? new ScanState { BlogUrl = blogUrl };
        var prog = new ScanProgress { AlbumsFound = state.AlbumsFound, Target = LinkTarget, PostsDone = state.PostsDone, Running = true };

        try
        {
            if (!IsBlogspot(blogUrl))
            {
                // Non-blogspot: single page only. Count unique albums, not links.
                foreach (var link in await FindLinksAsync(blogUrl, ct))
                {
                    ct.ThrowIfCancellationRequested();
                    var (_, isNew) = await ResolveAndAddAsync(link, entries, null, ct);
                    if (isNew)
                    {
                        prog.AlbumsFound++;
                        progress.Report(prog);
                    }
                    if (prog.AlbumsFound >= LinkTarget) break;
                }
                return;
            }

            int startIndex = state.StartIndex;
            while (prog.AlbumsFound < LinkTarget)
            {
                ct.ThrowIfCancellationRequested();
                var feedUrl = $"{NormalizeBlogUrl(blogUrl)}/feeds/posts/default?alt=json&max-results=150&start-index={startIndex}";
                using var doc = await HttpRetry.Run(async () =>
                {
                    using var r = await HttpRetry.Client.GetAsync(feedUrl, ct);
                    r.EnsureSuccessStatusCode();
                    var txt = await r.Content.ReadAsStringAsync(ct);
                    if (string.IsNullOrWhiteSpace(txt)) throw new HttpRequestException("Empty reply");
                    return JsonDocument.Parse(txt);
                }, ct: ct);

                var feed = doc.RootElement.GetProperty("feed");
                // Blogger's openSearch$totalResults.$t is a JSON STRING ("7057"), not a number.
                if (feed.TryGetProperty("openSearch$totalResults", out var total) &&
                    total.TryGetProperty("$t", out var tVal))
                {
                    int parsed = 0;
                    if (tVal.ValueKind == JsonValueKind.Number)
                        tVal.TryGetInt32(out parsed);
                    else if (tVal.ValueKind == JsonValueKind.String)
                        int.TryParse(tVal.GetString(), out parsed);
                    prog.TotalPosts = parsed;
                }

                if (!feed.TryGetProperty("entry", out var postEntries) || postEntries.GetArrayLength() == 0)
                    break; // exhausted

                int postsDoneInPage = 0;
                foreach (var e in postEntries.EnumerateArray())
                {
                    ct.ThrowIfCancellationRequested();
                    string content = e.TryGetProperty("content", out var c) ? c.GetProperty("$t").GetString() ?? "" : "";
                    int albumsThisPost = 0;
                    foreach (var link in ExtractLinks(content))
                    {
                        if (prog.AlbumsFound + albumsThisPost >= LinkTarget) break;
                        ct.ThrowIfCancellationRequested();
                        var (_, isNew) = await ResolveAndAddAsync(link, entries, null, ct);
                        if (isNew) albumsThisPost++;
                    }
                    // Commit per post: exact resume cursor, no double-count if stopped mid-post.
                    prog.AlbumsFound += albumsThisPost;
                    prog.PostsDone++;
                    postsDoneInPage++;
                    state.StartIndex = startIndex + postsDoneInPage;
                    state.AlbumsFound = prog.AlbumsFound;
                    state.PostsDone = prog.PostsDone;
                    state.SavedAt = DateTime.Now;
                    SaveState(state); // resumable across app restart, after every post
                    progress.Report(prog);
                    if (prog.AlbumsFound >= LinkTarget) break;
                }

                startIndex = state.StartIndex; // follow the exact cursor (page may be partial)
            }
        }
        finally
        {
            // Graceful halt: always persist latest position (Stop or target reached).
            state.PostsDone = prog.PostsDone;
            state.SavedAt = DateTime.Now;
            if (prog.AlbumsFound >= LinkTarget)
            {
                // Target reached: preserve the feed cursor for the NEXT 50,
                // reset the run counter (next Whole scan starts a fresh count).
                state.AlbumsFound = 0;
            }
            else
            {
                state.AlbumsFound = prog.AlbumsFound;
            }
            SaveState(state);
            prog.Running = false;
            prog.Note = prog.AlbumsFound >= LinkTarget ? "target reached" : "stopped";
            progress.Report(prog);
        }
    }

    /// <summary>
    /// Resolve one share link and merge into entries (dedup). Throws nothing: dead links are marked.
    /// Returns the entry and whether it is a NEW unique album (false for duplicates, dead, password).
    /// </summary>
    public async Task<(LeecherEntry Entry, bool IsNew)> ResolveAndAddAsync(
        string link, ConcurrentDictionary<string, LeecherEntry> entries, string? password, CancellationToken ct)
    {
        ResolvedShare resolved;
        try
        {
            resolved = await ShareResolver.ResolveAsync(link, password, ct);
        }
        catch (Exception)
        {
            // Dead after retries -> dead entry (auto-refresh of the share link happens
            // at download time via re-resolve; here we just record it).
            var dead = new LeecherEntry
            {
                DisplayName = link,
                Status = Models.EntryStatus.Dead,
                StatusText = "Link dead",
                PrimaryHost = ShareResolver.IsPCloud(link) ? "pcloud" : "mediafire",
            };
            if (ShareResolver.IsPCloud(link)) dead.PCloudShareUrl = link; else dead.MediaFireShareUrl = link;
            string dkey = (dead.IsFolder ? "F:" : "f:") + MyanmarText.NormalizeKey(link);
            dead.NormalizedKey = dkey;
            entries[dkey] = dead;
            return (dead, false);
        }

        if (resolved.NeedsPassword)
        {
            // Still locked (no password yet, or wrong password): return existing entry if any.
            var existing = entries.Values.FirstOrDefault(e =>
                string.Equals(e.PCloudShareUrl, link, StringComparison.Ordinal) ||
                string.Equals(e.MediaFireShareUrl, link, StringComparison.Ordinal));
            if (existing != null) return (existing, false);

            var pw = new LeecherEntry
            {
                DisplayName = link,
                Status = Models.EntryStatus.NeedsPassword,
                StatusText = "Password required",
                PrimaryHost = ShareResolver.IsPCloud(link) ? "pcloud" : "mediafire",
                IsFolder = true, // unknown until unlocked; corrected after unlock
            };
            if (ShareResolver.IsPCloud(link)) pw.PCloudShareUrl = link; else pw.MediaFireShareUrl = link;
            pw.NormalizedKey = "pw:" + MyanmarText.NormalizeKey(link);
            entries[pw.NormalizedKey] = pw;
            return (pw, false);
        }

        // Unlock migration: drop the temporary password entry for this URL (replaced by the real entry below).
        entries.TryRemove("pw:" + MyanmarText.NormalizeKey(link), out _);

        string key = (resolved.IsFolder ? "F:" : "f:") + MyanmarText.NormalizeKey(resolved.InsideName);
        // Atomic check-and-add: GetOrAdd guarantees a single stored instance per key even when
        // two scans resolve the same inside-name concurrently (previously both created an instance,
        // the dict kept one but the visible list showed both as duplicates).
        // isNew = true only for the thread that actually created the entry (unique album).
        bool isNew = false;
        var entry = entries.GetOrAdd(key, _ =>
        {
            isNew = true;
            var e = new LeecherEntry
            {
                DisplayName = resolved.InsideName,
                NormalizedKey = key,
                IsFolder = resolved.IsFolder,
                FileCount = resolved.Files.Count,
                TotalSize = resolved.Files.Sum(f => f.Size),
                PrimaryHost = resolved.Host,
                Status = Models.EntryStatus.Ready,
                StatusText = "Ready",
            };
            if (resolved.Host == "pcloud") e.PCloudShareUrl = link; else e.MediaFireShareUrl = link;
            foreach (var f in resolved.Files) e.Files.Add(f);
            return e;
        });
        // Dedup merge (idempotent for the creator thread): pCloud preferred as primary,
        // other host kept as backup. "Skip MediaFire" != discard: backup enables
        // auto-fallback if pCloud breaks (user spec).
        lock (entry)
        {
            if (resolved.Host == "pcloud")
            {
                entry.PCloudShareUrl = link;
                entry.PrimaryHost = "pcloud";
            }
            else
            {
                entry.MediaFireShareUrl = link;
                if (entry.PrimaryHost != "pcloud") entry.PrimaryHost = "mediafire";
            }
            // Unlock flow: password entry was created without files; merge them now.
            if (entry.Files.Count == 0 && resolved.Files.Count > 0)
            {
                foreach (var f in resolved.Files) entry.Files.Add(f);
                entry.FileCount = entry.Files.Count;
                entry.TotalSize = entry.Files.Sum(f => f.Size);
                if (entry.DisplayName == link || string.IsNullOrEmpty(entry.DisplayName))
                    entry.DisplayName = resolved.InsideName;
            }
            if (entry.Status == Models.EntryStatus.NeedsPassword && !resolved.NeedsPassword)
            {
                entry.Status = Models.EntryStatus.Ready;
                entry.StatusText = "Ready";
            }
            entry.RefreshDerived();
        }
        return (entry, isNew);
    }
}
