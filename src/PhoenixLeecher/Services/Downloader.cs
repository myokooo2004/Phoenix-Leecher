using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using PhoenixLeecher.Models;

namespace PhoenixLeecher.Services;

/// <summary>Throttle-safe progress tick; applied on the UI thread via IProgress.</summary>
public sealed class EntryTick
{
    public string EntryId { get; set; } = "";
    public Models.EntryStatus Status { get; set; }
    public long DownloadedBytes { get; set; }
    public double Progress { get; set; }
    public double SpeedBps { get; set; }
    public TimeSpan Elapsed { get; set; }
    public string StatusText { get; set; } = "";
}

/// <summary>
/// Download engine (user spec 2026-10-15).
/// Budgets: single connection per file (NO multi-segment), async streaming I/O,
/// max 3 concurrent file downloads (user-changeable), 32KB buffer, stream-to-disk
/// (never whole file in RAM), UI ticks throttled to 4/sec.
/// Behaviors:
/// - Pause/Resume per entry via HTTP Range (.part files).
/// - Expired/dead direct URL (403/401/410) -> auto re-resolve fresh URL, resume from offset.
/// - pCloud broken -> auto fallback to MediaFire backup (re-resolve + continue same files).
/// - Folder = 1 entry: all files into Downloads\PhoenixLeecher\&lt;folder&gt;\.
/// </summary>
public sealed class DownloadEngine
{
    private const int BufferSize = 32 * 1024;
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(250);

    private readonly SemaphoreSlim _fileSlots;
    public int MaxConcurrentFiles { get; }

    public DownloadEngine(int maxConcurrentFiles = 3)
    {
        MaxConcurrentFiles = maxConcurrentFiles;
        _fileSlots = new SemaphoreSlim(maxConcurrentFiles, maxConcurrentFiles);
    }

    public static string DownloadRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "PhoenixLeecher");

    public static string Sanitize(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Trim().TrimEnd('.');
    }

    public async Task DownloadAsync(LeecherEntry entry, IProgress<EntryTick> progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var elapsedBase = entry.Elapsed;
        long totalDownloaded = entry.DownloadedBytes;
        long grandTotal = Math.Max(1, entry.TotalSize);
        Report(EntryStatus.Downloading, "Starting…");

        string targetDir = DownloadRoot;
        if (entry.IsFolder) targetDir = Path.Combine(targetDir, Sanitize(entry.DisplayName));
        Directory.CreateDirectory(targetDir);

        var lastTick = DateTime.UtcNow;
        long lastBytes = totalDownloaded;
        var speedSamples = new Queue<(DateTime t, long b)>();

        void Tick(string? note = null)
        {
            var now = DateTime.UtcNow;
            speedSamples.Enqueue((now, totalDownloaded));
            while (speedSamples.Count > 0 && (now - speedSamples.Peek().t).TotalSeconds > 5)
                speedSamples.Dequeue();
            double speed = 0;
            if (speedSamples.Count >= 2)
            {
                var first = speedSamples.Peek();
                var span = (now - first.t).TotalSeconds;
                if (span > 0.2) speed = (totalDownloaded - first.b) / span;
            }
            Report(EntryStatus.Downloading,
                note ?? $"Downloading: {Math.Min(100, totalDownloaded * 100.0 / grandTotal):F0}%",
                speed);
            lastTick = now;
            lastBytes = totalDownloaded;
        }

        void Report(EntryStatus status, string text, double speed = 0)
        {
            progress.Report(new EntryTick
            {
                EntryId = entry.Id,
                Status = status,
                DownloadedBytes = totalDownloaded,
                Progress = Math.Min(100, totalDownloaded * 100.0 / grandTotal),
                SpeedBps = speed,
                Elapsed = elapsedBase + sw.Elapsed,
                StatusText = text,
            });
        }

        try
        {
            // Build per-file work items from the PRIMARY host.
            var work = await BuildWorkItemsAsync(entry, entry.PrimaryHost, targetDir, ct);

            var tasks = work.Select(w => DownloadFileAsync(entry, w, targetDir,
                bytes => { Interlocked.Add(ref totalDownloaded, bytes); },
                () => { if (DateTime.UtcNow - lastTick >= TickInterval) Tick(); },
                ct)).ToArray();
            await Task.WhenAll(tasks);

            Report(EntryStatus.Completed, "Completed");
        }
        catch (OperationCanceledException)
        {
            Report(EntryStatus.Paused, $"Paused: {Math.Min(100, totalDownloaded * 100.0 / grandTotal):F0}%");
        }
        catch (Exception ex)
        {
            Report(EntryStatus.Failed, "Failed: " + ex.Message);
        }
    }

    private sealed class FileWork
    {
        public RemoteFile Remote = null!;
        public string LocalPath = "";
        public Func<CancellationToken, Task<string>> ResolveUrl = null!;
    }

    private async Task<List<FileWork>> BuildWorkItemsAsync(LeecherEntry entry, string host, string targetDir, CancellationToken ct)
    {
        var list = new List<FileWork>();
        if (host == "pcloud" && entry.PCloudShareUrl != null)
        {
            string code = ShareResolver.PCloudCodeOf(entry.PCloudShareUrl);
            foreach (var f in entry.Files)
            {
                string fid = f.RefId;
                list.Add(new FileWork
                {
                    Remote = f,
                    LocalPath = Path.Combine(targetDir, Sanitize(f.Name)),
                    ResolveUrl = t => ShareResolver.PCloudDirectUrlAsync(code, fid, entry.Password, t),
                });
            }
        }
        else if (host == "mediafire" && entry.MediaFireShareUrl != null)
        {
            // Re-resolve MediaFire share to map quickkeys by normalized file name.
            var mf = await ShareResolver.ResolveAsync(entry.MediaFireShareUrl, null, ct);
            var byName = mf.Files.ToDictionary(f => MyanmarText.NormalizeKey(f.Name));
            foreach (var f in entry.Files)
            {
                string qk = f.RefId;
                if (byName.TryGetValue(MyanmarText.NormalizeKey(f.Name), out var m)) qk = m.RefId;
                list.Add(new FileWork
                {
                    Remote = f,
                    LocalPath = Path.Combine(targetDir, Sanitize(f.Name)),
                    ResolveUrl = t => ShareResolver.MediaFireDirectUrlAsync(qk, t),
                });
            }
        }
        else throw new InvalidOperationException($"No {host} share URL for entry");
        return list;
    }

    private async Task DownloadFileAsync(LeecherEntry entry, FileWork w, string targetDir,
        Action<long> addBytes, Action maybeTick, CancellationToken ct)
    {
        await _fileSlots.WaitAsync(ct);
        try
        {
            string partPath = w.LocalPath + ".part";
            string finalPath = w.LocalPath;
            // Already fully downloaded in a previous run? skip.
            if (File.Exists(finalPath) && !File.Exists(partPath))
            {
                long fl = new FileInfo(finalPath).Length;
                if (w.Remote.Size <= 0 || fl >= w.Remote.Size)
                {
                    addBytes(0); // no change; entry totals already account via entry.DownloadedBytes
                    return;
                }
                // Incomplete final without .part -> turn into .part and resume.
                File.Move(finalPath, partPath);
            }

            bool fellBack = false;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                long offset = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
                // Already fully downloaded?
                if (w.Remote.Size > 0 && offset >= w.Remote.Size)
                {
                    MovePartToFinal(partPath, finalPath);
                    return;
                }

                string url;
                try { url = await w.ResolveUrl(ct); }
                catch (Exception) when (attempt < 2)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2 << attempt), ct);
                    continue;
                }

                try
                {
                    long corrected = await StreamToFileAsync(url, partPath, finalPath, offset, addBytes, maybeTick, ct);
                    if (corrected < 0) addBytes(corrected); // server ignored Range: counter correction
                    return; // success
                }
                catch (HttpRequestException ex) when (
                    ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized or HttpStatusCode.Gone)
                {
                    // Expired/dead direct URL -> auto re-resolve fresh URL, resume from offset (user spec).
                    if (attempt == 2) throw;
                    await Task.Delay(TimeSpan.FromSeconds(1), ct);
                    continue;
                }
                catch (HttpRequestException) when (!fellBack && entry.HasBackup && entry.PrimaryHost == "pcloud")
                {
                    // pCloud broken -> auto fallback to MediaFire backup (user spec).
                    fellBack = true;
                    var mfWork = (await BuildWorkItemsAsync(entry, "mediafire", targetDir, ct))
                        .FirstOrDefault(x => MyanmarText.NormalizeKey(x.Remote.Name) == MyanmarText.NormalizeKey(w.Remote.Name));
                    if (mfWork == null) throw;
                    w.ResolveUrl = mfWork.ResolveUrl;
                    continue;
                }
            }
            throw new HttpRequestException("Download failed after retries");
        }
        finally
        {
            _fileSlots.Release();
        }
    }

    /// <returns>Negative correction to apply to the byte counter (server ignored Range), else 0.</returns>
    private static async Task<long> StreamToFileAsync(string url, string partPath, string finalPath,
        long offset, Action<long> addBytes, Action maybeTick, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (offset > 0) req.Headers.Range = new RangeHeaderValue(offset, null);
        // NOTE: DownloadClient (no overall timeout) — Client's 30s timeout would kill large files.
        using var resp = await HttpRetry.DownloadClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            MovePartToFinal(partPath, finalPath); // already complete
            return 0;
        }
        resp.EnsureSuccessStatusCode();

        bool resumed = resp.StatusCode == HttpStatusCode.PartialContent && offset > 0;
        long correction = 0;
        using var fs = new FileStream(partPath, resumed ? FileMode.Append : FileMode.Create,
            FileAccess.Write, FileShare.None, BufferSize, useAsync: true);
        if (!resumed && offset > 0)
        {
            // Server ignored Range: restart from scratch; counter already held `offset` bytes.
            correction = -offset;
            fs.SetLength(0);
        }
        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[BufferSize];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            await fs.WriteAsync(buffer.AsMemory(0, read), ct);
            addBytes(read);
            maybeTick();
        }
        await fs.FlushAsync(ct);
        // Dispose the file stream BEFORE renaming: on Windows an open handle
        // (FileShare.None) blocks File.Move, which left a stray .part behind
        // and marked the entry Failed even at 100%.
        await fs.DisposeAsync();
        await stream.DisposeAsync();
        // Completed file -> move .part to final (if we know the size, or always on clean finish)
        MovePartToFinal(partPath, finalPath);
        return correction;
    }

    private static void MovePartToFinal(string partPath, string finalPath)
    {
        if (!File.Exists(partPath)) return;
        if (File.Exists(finalPath)) File.Delete(finalPath);
        File.Move(partPath, finalPath);
    }
}
