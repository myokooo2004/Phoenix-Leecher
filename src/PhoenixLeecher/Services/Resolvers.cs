using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using PhoenixLeecher.Models;

namespace PhoenixLeecher.Services;

/// <summary>
/// Result of resolving a share link to its INSIDE name + file list.
/// Names always come from the host API ("inside"), never from the URL slug (user spec).
/// </summary>
public sealed class ResolvedShare
{
    public string Host { get; set; } = "";          // "pcloud" | "mediafire"
    public string ShareUrl { get; set; } = "";
    public string InsideName { get; set; } = "";
    public bool IsFolder { get; set; }
    public List<RemoteFile> Files { get; } = new();
    public bool NeedsPassword { get; set; }
}

public static class HttpRetry
{
    // 6 attempts, exponential backoff. pCloud intermittently returns empty
    // replies (~24% observed 2026-10-15); this is REQUIRED, not optional.
    public static async Task<T> Run<T>(Func<Task<T>> fn, int attempts = 6, CancellationToken ct = default)
    {
        Exception? last = null;
        for (int i = 0; i < attempts; i++)
        {
            ct.ThrowIfCancellationRequested();
            try { return await fn(); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, i)), ct);
            }
        }
        throw last!;
    }

    public static readonly HttpClient Client = new(new HttpClientHandler
    {
        AllowAutoRedirect = true,
        AutomaticDecompression = DecompressionMethods.All,
    })
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    /// <summary>Separate client for file downloads: no overall timeout (large files).</summary>
    public static readonly HttpClient DownloadClient = new(new HttpClientHandler
    {
        AllowAutoRedirect = true,
        AutomaticDecompression = DecompressionMethods.All,
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    static HttpRetry()
    {
        Client.DefaultRequestHeaders.UserAgent.ParseAdd(Ua);
        DownloadClient.DefaultRequestHeaders.UserAgent.ParseAdd(Ua);
    }

    private const string Ua =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36";
}

public static class ShareResolver
{
    private static readonly Regex PCloudCode = new(@"pcloud\.link/publink/show\?code=([A-Za-z0-9]+)", RegexOptions.Compiled);
    private static readonly Regex MfFolderKey = new(@"mediafire\.com/folder/([A-Za-z0-9]+)", RegexOptions.Compiled);
    private static readonly Regex MfFileKey = new(@"mediafire\.com/file/([A-Za-z0-9]+)", RegexOptions.Compiled);

    public static bool IsPCloud(string url) => url.Contains("pcloud.link");
    public static bool IsMediaFire(string url) => url.Contains("mediafire.com");

    public static async Task<ResolvedShare> ResolveAsync(string url, string? password, CancellationToken ct)
    {
        if (IsPCloud(url)) return await ResolvePCloudAsync(url, password, ct);
        if (IsMediaFire(url)) return await ResolveMediaFireAsync(url, ct);
        throw new NotSupportedException("Unsupported host: " + url);
    }

    // ---------------- pCloud ----------------
    // Verified 2026-10-15: showpublink -> metadata{ name, isfolder, contents[{name,size,fileid}] }
    // getpublinkdownload?code=&fileid= -> { hosts[], path } ; folderid rejected -> per-file links.
    private static async Task<ResolvedShare> ResolvePCloudAsync(string url, string? password, CancellationToken ct)
    {
        var code = PCloudCode.Match(url).Groups[1].Value;
        if (string.IsNullOrEmpty(code)) throw new ArgumentException("No pCloud code in URL");

        var api = $"https://api.pcloud.com/showpublink?code={code}";
        if (!string.IsNullOrEmpty(password)) api += "&linkpassword=" + HttpUtility.UrlEncode(password);

        var doc = await HttpRetry.Run(async () =>
        {
            using var r = await HttpRetry.Client.GetAsync(api, ct);
            r.EnsureSuccessStatusCode();
            var txt = await r.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(txt)) throw new HttpRequestException("Empty reply");
            return JsonDocument.Parse(txt);
        }, ct: ct);

        var root = doc.RootElement;
        int result = root.GetProperty("result").GetInt32();
        string errMsg = root.TryGetProperty("error", out var em) ? em.GetString() ?? "" : "";
        // Password-protected link detection: pCloud signals via error mentioning password.
        // Exact result code unverified live (needs a real password link) -> message heuristic.
        // TODO(verify): confirm with a real password-protected publink during PC testing.
        if (result != 0 && errMsg.Contains("password", StringComparison.OrdinalIgnoreCase))
            return new ResolvedShare { Host = "pcloud", ShareUrl = url, NeedsPassword = true };
        if (result != 0)
            throw new HttpRequestException("pCloud error: " + errMsg);

        var meta = root.GetProperty("metadata");
        var share = new ResolvedShare
        {
            Host = "pcloud",
            ShareUrl = url,
            InsideName = MyanmarText.ForDisplay(meta.GetProperty("name").GetString() ?? code),
            IsFolder = meta.TryGetProperty("isfolder", out var f) && f.GetBoolean(),
        };
        if (share.IsFolder && meta.TryGetProperty("contents", out var contents))
        {
            foreach (var c in contents.EnumerateArray())
            {
                share.Files.Add(new RemoteFile
                {
                    Name = MyanmarText.ForDisplay(c.GetProperty("name").GetString() ?? ""),
                    Size = c.TryGetProperty("size", out var s) ? s.GetInt64() : 0,
                    RefId = c.TryGetProperty("fileid", out var id) ? id.GetInt64().ToString() : "",
                });
            }
        }
        else
        {
            share.Files.Add(new RemoteFile
            {
                Name = share.InsideName,
                Size = meta.TryGetProperty("size", out var s) ? s.GetInt64() : 0,
                RefId = meta.TryGetProperty("fileid", out var id) ? id.GetInt64().ToString() : "",
            });
        }
        return share;
    }

    /// <summary>Direct (expiring) download URL for one pCloud file. Verified 2026-10-15 (Range: 206).</summary>
    public static async Task<string> PCloudDirectUrlAsync(string code, string fileId, string? password, CancellationToken ct)
    {
        var api = $"https://api.pcloud.com/getpublinkdownload?code={code}&fileid={fileId}";
        if (!string.IsNullOrEmpty(password)) api += "&linkpassword=" + HttpUtility.UrlEncode(password);
        var doc = await HttpRetry.Run(async () =>
        {
            using var r = await HttpRetry.Client.GetAsync(api, ct);
            r.EnsureSuccessStatusCode();
            var txt = await r.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(txt)) throw new HttpRequestException("Empty reply");
            return JsonDocument.Parse(txt);
        }, ct: ct);
        var root = doc.RootElement;
        if (root.GetProperty("result").GetInt32() != 0)
            throw new HttpRequestException("pCloud download error: " + root.GetProperty("error").GetString());
        string host = root.GetProperty("hosts")[0].GetString()!;
        string path = root.GetProperty("path").GetString()!;
        return $"https://{host}{path}";
    }

    public static string PCloudCodeOf(string shareUrl) => PCloudCode.Match(shareUrl).Groups[1].Value;

    // ---------------- MediaFire ----------------
    // Verified 2026-10-15 (anonymous, no session):
    //   get_info.php?folder_key= -> folder_info{ name, file_count }
    //   get_content.php?folder_key=&content_type=files -> files[{ filename, size, quickkey }]
    // File download: GET /file/{quickkey}/x (no redirect) -> 302 Location -> direct (Range: 206).
    private static async Task<ResolvedShare> ResolveMediaFireAsync(string url, CancellationToken ct)
    {
        var fm = MfFolderKey.Match(url);
        if (fm.Success)
        {
            string key = fm.Groups[1].Value;
            var infoDoc = await GetMfApiAsync($"https://www.mediafire.com/api/1.4/folder/get_info.php?folder_key={key}&response_format=json", ct);
            var info = infoDoc.RootElement.GetProperty("response").GetProperty("folder_info");
            var share = new ResolvedShare
            {
                Host = "mediafire",
                ShareUrl = url,
                InsideName = MyanmarText.ForDisplay(info.GetProperty("name").GetString() ?? key),
                IsFolder = true,
            };
            var contDoc = await GetMfApiAsync($"https://www.mediafire.com/api/1.4/folder/get_content.php?folder_key={key}&content_type=files&response_format=json", ct);
            var files = contDoc.RootElement.GetProperty("response").GetProperty("folder_content").GetProperty("files");
            foreach (var f in files.EnumerateArray())
            {
                long size = 0;
                if (f.TryGetProperty("size", out var s))
                {
                    if (s.ValueKind == JsonValueKind.String) long.TryParse(s.GetString(), out size);
                    else if (s.ValueKind == JsonValueKind.Number) size = s.GetInt64();
                }
                share.Files.Add(new RemoteFile
                {
                    Name = MyanmarText.ForDisplay(f.GetProperty("filename").GetString() ?? ""),
                    Size = size,
                    RefId = f.GetProperty("quickkey").GetString() ?? "",
                });
            }
            return share;
        }

        var fileM = MfFileKey.Match(url);
        if (fileM.Success)
        {
            // Single file link: inside-name via file page title fallback; quickkey from URL.
            string key = fileM.Groups[1].Value;
            string name = await MediaFireFileNameAsync(url, ct) ?? key;
            // Guard (2026-10-16): if the title fetch failed, the fallback is the URL's last
            // segment — for ".../file/{key}/file" that's literally "file". A 0-byte entry
            // named "file" is never useful; treat as unresolvable (becomes a Dead row).
            if (string.IsNullOrWhiteSpace(name) || name.Equals("file", StringComparison.OrdinalIgnoreCase))
                throw new HttpRequestException("Could not determine MediaFire file name");
            // Size via HEAD on the direct URL (best effort; user request 2026-10-16).
            // Without this, single files show 0.0 B and download % is meaningless.
            long size = 0;
            try
            {
                string direct = await MediaFireDirectUrlAsync(key, ct);
                using var headReq = new HttpRequestMessage(HttpMethod.Head, direct);
                using var headResp = await HttpRetry.Client.SendAsync(headReq, ct);
                if (headResp.Content.Headers.ContentLength is long len && len > 0)
                    size = len;
            }
            catch { /* size stays 0; download still works */ }
            return new ResolvedShare
            {
                Host = "mediafire",
                ShareUrl = url,
                InsideName = MyanmarText.ForDisplay(name),
                IsFolder = false,
                Files = { new RemoteFile { Name = MyanmarText.ForDisplay(name), Size = size, RefId = key } },
            };
        }
        throw new ArgumentException("Unrecognized MediaFire URL");
    }

    private static async Task<JsonDocument> GetMfApiAsync(string api, CancellationToken ct) =>
        await HttpRetry.Run(async () =>
        {
            using var r = await HttpRetry.Client.GetAsync(api, ct);
            r.EnsureSuccessStatusCode();
            var txt = await r.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(txt)) throw new HttpRequestException("Empty reply");
            return JsonDocument.Parse(txt);
        }, ct: ct);

    private static async Task<string?> MediaFireFileNameAsync(string fileUrl, CancellationToken ct)
    {
        try
        {
            var html = await HttpRetry.Run(() => HttpRetry.Client.GetStringAsync(fileUrl, ct), attempts: 3, ct: ct);
            var m = Regex.Match(html, @"<title>\s*(.*?)\s*</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (m.Success)
            {
                var t = HttpUtility.HtmlDecode(m.Groups[1].Value).Trim();
                // Titles look like "filename - MediaFire"
                if (t.EndsWith(" - MediaFire")) t = t[..^" - MediaFire".Length];
                if (!string.IsNullOrWhiteSpace(t)) return t;
            }
        }
        catch { /* fall through to URL segment */ }
        var seg = fileUrl.TrimEnd('/').Split('/');
        // MediaFire file URLs are /file/{key}/{filename}; some blog links append a trailing
        // "/file" (e.g. .../song.mp3/file) — then the real name is the second-to-last segment.
        // Verified 2026-10-16: phyuniwarpyar.blogspot.com links carry the .mp3 name in the URL.
        string candidate = seg.Length >= 1 ? seg[^1] : "";
        if (candidate.Equals("file", StringComparison.OrdinalIgnoreCase) && seg.Length >= 2)
            candidate = seg[^2];
        candidate = HttpUtility.UrlDecode(candidate);
        return string.IsNullOrWhiteSpace(candidate) ? null : candidate;
    }

    /// <summary>Direct (expiring, 302) download URL for one MediaFire file. Verified 2026-10-15.</summary>
    public static async Task<string> MediaFireDirectUrlAsync(string quickKey, CancellationToken ct)
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36");
        using var r = await client.GetAsync($"https://www.mediafire.com/file/{quickKey}/x", ct);
        if (r.StatusCode is HttpStatusCode.Found or HttpStatusCode.Moved or HttpStatusCode.Redirect)
        {
            var loc = r.Headers.Location?.ToString();
            if (!string.IsNullOrEmpty(loc)) return loc;
        }
        throw new HttpRequestException("MediaFire did not return a download redirect");
    }
}
