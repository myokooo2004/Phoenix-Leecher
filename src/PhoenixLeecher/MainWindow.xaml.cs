using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PhoenixLeecher.Models;
using PhoenixLeecher.Services;

namespace PhoenixLeecher;

public partial class MainWindow : Window
{
    private readonly ConcurrentDictionary<string, LeecherEntry> _entries = new();
    private readonly ObservableCollection<LeecherEntry> _rows = new();
    private readonly Dictionary<string, LeecherEntry> _byId = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _dlCts = new();
    private readonly ConcurrentDictionary<string, Task> _dlTasks = new();
    private bool _isBatchRunning;
    private readonly List<string> _bothFailedNames = new(); // guarded by lock(_bothFailedNames)
    private readonly Dictionary<string, string> _passwords = new();
    private readonly LinkScanner _scanner = new();
    private DownloadEngine _engine = new(maxConcurrentFiles: 3);
    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _dlBatchCts; // Download All batch token; Pause All cancels it too
    private bool _scanRunning;
    private static string EntriesPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PhoenixLeecher", "entries.json");
    private readonly IProgress<EntryTick> _tickProgress;
    private readonly IProgress<ScanProgress> _scanProgress;
    private readonly DispatcherTimer _syncTimer = new();

    public MainWindow()
    {
        InitializeComponent();
        Grid.ItemsSource = _rows;

        _tickProgress = new Progress<EntryTick>(ApplyTick);
        _scanProgress = new Progress<ScanProgress>(ApplyScanProgress);

        _syncTimer.Interval = TimeSpan.FromMilliseconds(500);
        _syncTimer.Tick += (_, _) => SyncRows();
        _syncTimer.Start();

        StatusFolder.Text = DownloadEngine.DownloadRoot;
        LoadEntries(); // restore list from previous session (no auto-download)
        UpdateCounts();
        Closing += (_, _) => SaveEntries();
    }

    // ---------- rows sync (UI thread) ----------
    private void SyncRows()
    {
        foreach (var kv in _entries)
        {
            if (!_byId.ContainsKey(kv.Value.Id))
            {
                _byId[kv.Value.Id] = kv.Value;
                _rows.Add(kv.Value);
            }
        }
        // removals
        for (int i = _rows.Count - 1; i >= 0; i--)
            if (!_byId.ContainsKey(_rows[i].Id))
            {
                _byId.Remove(_rows[i].Id);
                _rows.RemoveAt(i);
            }
        UpdateCounts();
    }

    private void UpdateCounts()
    {
        int done = _rows.Count(e => e.Status == EntryStatus.Completed);
        double totalSpeed = _rows.Where(e => e.Status == EntryStatus.Downloading).Sum(e => e.SpeedBps);
        StatusCounts.Text = $"Complete downloads {done} of {_rows.Count}";
        StatusSpeed.Text = totalSpeed > 0 ? $"Current speed: {FormatSpeed(totalSpeed)}" : "";
        EmptyState.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string FormatSpeed(double bps)
    {
        string[] u = { "B", "KB", "MB", "GB" };
        double d = bps; int i = 0;
        while (d >= 1024 && i < u.Length - 1) { d /= 1024; i++; }
        return $"{d:F1} {u[i]}/s";
    }

    private void ApplyTick(EntryTick t)
    {
        if (!_byId.TryGetValue(t.EntryId, out var e)) return;
        e.Status = t.Status;
        e.DownloadedBytes = t.DownloadedBytes;
        e.Progress = t.Progress;
        e.SpeedBps = t.SpeedBps;
        e.Elapsed = t.Elapsed;
        e.StatusText = t.StatusText;
        if (t.Status is EntryStatus.Completed or EntryStatus.Failed or EntryStatus.Paused)
        {
            if (_dlCts.Remove(t.EntryId, out var cts)) cts.Dispose();
            // Both-links-failed: pCloud tried, MediaFire backup tried, both failed (user spec 2026-10-16).
            if (t.Status == EntryStatus.Failed && e.BothLinksFailed)
            {
                lock (_bothFailedNames) _bothFailedNames.Add(e.DisplayName);
                if (!_isBatchRunning)
                {
                    // Single download: notify immediately.
                    MessageBox.Show($"Album '{e.DisplayName}': pCloud and MediaFire links both failed.",
                        "Phoenix Leecher", MessageBoxButton.OK, MessageBoxImage.Warning);
                    lock (_bothFailedNames) _bothFailedNames.Clear();
                }
                // Batch: DownloadAll_Click shows one summary at the end.
            }
            SaveEntries();
        }
        UpdateCounts();
    }

    private void ApplyScanProgress(ScanProgress p)
    {
        ScanBar.Maximum = p.Target;
        ScanBar.Value = p.AlbumsFound;
        ScanText.Text = p.Running
            ? $"Scanning older posts… {p.AlbumsFound}/{p.Target} albums" +
              (p.TotalPosts > 0 ? $" · post {p.PostsDone}/{p.TotalPosts}" : $" · {p.PostsDone} posts")
            : $"Scan {p.Note}: {p.AlbumsFound}/{p.Target} albums";
        if (!p.Running)
        {
            ScanBtn.IsEnabled = true;
            StopBtn.IsEnabled = false;
            // Target reached -> cursor preserved + counter reset by Scanner (next Whole scan
            // finds the NEXT 50). Exhausted -> clear (fresh start may find new posts).
            // Stopped early -> keep for Resume.
            if (p.AlbumsFound >= p.Target)
            {
                ScanBtnLabel.Text = "Whole scan";
            }
            else if (p.Note.Contains("exhaust"))
            {
                _scanner.ClearState(UrlBox.Text.Trim());
                ScanBtnLabel.Text = "Whole scan";
            }
            else if (LinkScanner.IsBlogspot(UrlBox.Text.Trim()))
            {
                ScanBtnLabel.Text = $"Resume scan ({p.AlbumsFound}/{p.Target})";
            }
        }
    }

    private string CurrentUrl => UrlBox.Text.Trim();

    // ---------- Find Links (immediate, page only) ----------
    // ---------- Find Links (immediate, page only). Returns new unique albums found. ----------
    private async Task<int> DoFindLinksAsync()
    {
        string url = CurrentUrl; // capture on UI thread: UrlBox cannot be read from a worker thread
        if (string.IsNullOrWhiteSpace(url)) return 0;
        var links = await Task.Run(() => _scanner.FindLinksAsync(url, CancellationToken.None));
        int newAlbums = 0;
        await Task.Run(async () =>
        {
            foreach (var link in links)
            {
                var (_, isNew) = await _scanner.ResolveAndAddAsync(link, _entries, null, CancellationToken.None);
                if (isNew) newAlbums++;
            }
        });
        SyncRows();
        return newAlbums;
    }

    // ---------- Page scan (immediate, page only) ----------
    private async void PageScan_Click(object sender, RoutedEventArgs e)
    {
        if (_scanRunning) return;
        SetScanRunning(true);
        try { await DoFindLinksAsync(); }
        catch (Exception ex)
        {
            MessageBox.Show("Page scan failed: " + ex.Message, "Phoenix Leecher",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { SaveEntries(); SetScanRunning(false); }
    }

    // ---------- Whole scan (page + older posts, 50 NEW albums, resumable) ----------
    private async void WholeScan_Click(object sender, RoutedEventArgs e)
    {
        if (_scanRunning) return;
        string url = CurrentUrl; // capture on UI thread: UrlBox cannot be read from a worker thread
        if (string.IsNullOrWhiteSpace(url)) return;
        SetScanRunning(true);
        try
        {
            // Run model (user spec 2026-10-16):
            // - Fresh: page links first (counted), then older-posts crawl to 50 NEW albums.
            // - Stopped early (AlbumsFound in 1..49): resume from saved cursor, no page re-scrape.
            // - Target reached (previous run hit 50): NEW run — cursor preserved, counter reset,
            //   next 50 NEW albums; already-seen links are not re-counted (dedup).
            var saved = _scanner.LoadState(url);
            bool isBlog = LinkScanner.IsBlogspot(url);
            bool targetReached = saved != null && saved.AlbumsFound >= LinkScanner.LinkTarget;
            bool isResume = saved != null && !targetReached && saved.AlbumsFound > 0 && isBlog;

            if (targetReached)
            {
                // Previous run completed 50: reset counter, keep cursor for the next 50.
                saved!.AlbumsFound = 0;
                _scanner.SaveState(saved);
                isResume = true; // skip page re-scrape, continue from cursor
            }

            if (!isResume)
            {
                int pageAlbums = await DoFindLinksAsync();
                // Seed the run counter with page albums so the feed crawl continues toward 50.
                var s = _scanner.LoadState(url) ?? new ScanState { BlogUrl = url };
                s.AlbumsFound = pageAlbums;
                s.StartIndex = 1;
                _scanner.SaveState(s);
            }

            _scanCts = new CancellationTokenSource();
            StopBtn.IsEnabled = true;
            ScanStrip.Visibility = Visibility.Visible;
            await Task.Run(() => _scanner.ScanAsync(url, _entries, _scanProgress, _scanCts.Token));
        }
        catch (OperationCanceledException) { /* Stop pressed: state already saved */ }
        catch (Exception ex)
        {
            MessageBox.Show("Scan failed: " + ex.Message, "Phoenix Leecher",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            SyncRows();
            SaveEntries();
            _scanCts?.Dispose();
            _scanCts = null;
            SetScanRunning(false);
        }
    }

    private void SetScanRunning(bool running)
    {
        _scanRunning = running;
        PageScanBtn.IsEnabled = !running;
        ScanBtn.IsEnabled = !running;
        StopBtn.IsEnabled = false; // enabled only by Whole scan below
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _scanCts?.Cancel();
        StopBtn.IsEnabled = false;
    }

    // ---------- downloads ----------
    /// <summary>Starts a download if possible. Returns true if a download was started.</summary>
    private bool StartDownloadNow(LeecherEntry entry)
    {
        if (entry.Status == EntryStatus.Downloading) return false;
        if (entry.Status == EntryStatus.NeedsPassword && string.IsNullOrEmpty(entry.Password))
        {
            MessageBox.Show("This link needs a password — enter it in the row and press Unlock.",
                "Phoenix Leecher", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        if (_dlCts.ContainsKey(entry.Id)) return false;
        var cts = new CancellationTokenSource();
        _dlCts[entry.Id] = cts;
        var task = _engine.DownloadAsync(entry, _tickProgress, cts.Token);
        _dlTasks[entry.Id] = task;
        // Clean up the task slot when done (ApplyTick removes the CTS on terminal state).
        _ = task.ContinueWith(_ => _dlTasks.TryRemove(entry.Id, out _), TaskScheduler.Default);
        return true;
    }

    private void StartDownload(LeecherEntry entry) => StartDownloadNow(entry);

    private void RowDownload_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is LeecherEntry entry)
            StartDownload(entry);
    }

    private void RowPause_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is LeecherEntry entry &&
            _dlCts.TryGetValue(entry.Id, out var cts))
            cts.Cancel();
    }

    private async void DownloadAll_Click(object sender, RoutedEventArgs e)
    {
        // Cancel any previous batch, then run a new one. Pause All cancels this
        // batch token too, so the loop stops instead of starting the next file.
        _dlBatchCts?.Cancel();
        _dlBatchCts?.Dispose();
        var batchCts = new CancellationTokenSource();
        _dlBatchCts = batchCts;
        _isBatchRunning = true;
        try
        {
            var list = _rows.Where(x => x.Status is EntryStatus.Ready or EntryStatus.Paused or EntryStatus.Failed).ToList();
            foreach (var entry in list)
            {
                batchCts.Token.ThrowIfCancellationRequested();
                // Start on the UI thread; skip if not started (already running / needs password).
                bool started = (bool)Dispatcher.Invoke(() => StartDownloadNow(entry));
                if (!started) continue;
                // Wait for this entry's download task (or batch cancel) — no polling.
                if (_dlTasks.TryGetValue(entry.Id, out var task))
                    await Task.WhenAny(task, Task.Delay(Timeout.Infinite, batchCts.Token));
            }
        }
        catch (OperationCanceledException) { /* Pause All pressed */ }
        finally
        {
            _isBatchRunning = false;
            if (_dlBatchCts == batchCts) { _dlBatchCts = null; batchCts.Dispose(); }
            // Batch summary: albums where BOTH pCloud and MediaFire failed.
            List<string> failed;
            lock (_bothFailedNames) { failed = new List<string>(_bothFailedNames); _bothFailedNames.Clear(); }
            if (failed.Count > 0)
                MessageBox.Show("These albums failed on both pCloud and MediaFire:\n- " + string.Join("\n- ", failed),
                    "Phoenix Leecher", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void PauseAll_Click(object sender, RoutedEventArgs e)
    {
        // Stop everything: the Download All batch loop plus every running file.
        _dlBatchCts?.Cancel();
        foreach (var cts in _dlCts.Values.ToList()) cts.Cancel();
    }

    private void ResumeAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var entry in _rows.Where(x => x.Status is EntryStatus.Paused or EntryStatus.Failed).ToList())
            StartDownload(entry);
    }

    private void RemoveEntry(LeecherEntry entry)
    {
        if (_dlCts.TryGetValue(entry.Id, out var cts)) cts.Cancel();
        _entries.TryRemove(entry.NormalizedKey, out _);
        _byId.Remove(entry.Id);
        _rows.Remove(entry);
        _passwords.Remove(entry.Id);
        SaveEntries();
        UpdateCounts();
    }

    private void RowDelete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is LeecherEntry entry)
            RemoveEntry(entry);
    }

    private void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is LeecherEntry entry)
            RemoveEntry(entry);
    }

    private void RemoveCompleted_Click(object sender, RoutedEventArgs e)
    {
        // List only: the downloaded files stay on disk.
        foreach (var entry in _rows.Where(x => x.Status == EntryStatus.Completed).ToList())
            RemoveEntry(entry);
    }

    private void Grid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Click on empty area (not on a row) -> clear the blue selection.
        DependencyObject? dep = e.OriginalSource as DependencyObject;
        while (dep != null && dep is not DataGridRow && dep is not DataGridCell
               && dep is not System.Windows.Controls.Primitives.ScrollBar && dep != Grid)
            dep = VisualTreeHelper.GetParent(dep);
        if (dep == Grid || dep == null)
            Grid.UnselectAll();
    }

    // ---------- password (session-only, never persisted) ----------
    private void PwBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox pb && pb.DataContext is LeecherEntry entry)
            _passwords[entry.Id] = pb.Password;
    }

    private async void Unlock_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not LeecherEntry entry) return;
        _passwords.TryGetValue(entry.Id, out var pw);
        entry.Status = EntryStatus.Resolving;
        entry.StatusText = "Unlocking…";
        try
        {
            string? url = entry.PrimaryHost == "pcloud" ? entry.PCloudShareUrl : entry.MediaFireShareUrl;
            if (url == null)
            {
                entry.Status = EntryStatus.NeedsPassword;
                entry.StatusText = "Password required";
                return;
            }
            var (fresh, _) = await Task.Run(() => _scanner.ResolveAndAddAsync(url, _entries, pw, CancellationToken.None));
            SyncRows();
            if (fresh.Status == EntryStatus.NeedsPassword)
            {
                MessageBox.Show("Wrong password.", "Phoenix Leecher", MessageBoxButton.OK, MessageBoxImage.Warning);
                entry.Status = EntryStatus.NeedsPassword;
                entry.StatusText = "Password required";
            }
            else
            {
                fresh.Password = pw; // session only, never persisted
            }
        }
        catch (Exception ex)
        {
            entry.Status = EntryStatus.Failed;
            entry.StatusText = "Unlock failed: " + ex.Message;
        }
    }

    // ---------- theme ----------
    private void ThemeToggle_Click(object sender, RoutedEventArgs e)
    {
        if (App.ThemeName == "Light") ApplyDark(); else ApplyLight();
    }
    private void ThemeLight_Click(object sender, RoutedEventArgs e) => ApplyLight();
    private void ThemeDark_Click(object sender, RoutedEventArgs e) => ApplyDark();
    private void ApplyLight()
    {
        App.ApplyTheme("Light");
        ThemeBtnLabel.Text = "Dark";
        MenuLight.IsChecked = true; MenuDark.IsChecked = false;
    }
    private void ApplyDark()
    {
        App.ApplyTheme("Dark");
        ThemeBtnLabel.Text = "Light";
        MenuLight.IsChecked = false; MenuDark.IsChecked = true;
    }

    // ---------- misc ----------
    private void Concurrency_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && int.TryParse(mi.Tag?.ToString(), out int n))
        {
            _engine = new DownloadEngine(maxConcurrentFiles: n);
            foreach (var item in ((mi.Parent as MenuItem)?.Items.OfType<MenuItem>() ?? Enumerable.Empty<MenuItem>()))
                item.IsChecked = item == mi;
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(DownloadEngine.DownloadRoot);
        Process.Start("explorer.exe", DownloadEngine.DownloadRoot);
    }

    private void About_Click(object sender, RoutedEventArgs e) =>
        new AboutWindow { Owner = this }.ShowDialog();

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    // ---------- entry list persistence (user spec: list survives app restart) ----------
    private sealed class SavedEntry
    {
        public string NormalizedKey { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public bool IsFolder { get; set; }
        public long TotalSize { get; set; }
        public string PrimaryHost { get; set; } = "pcloud";
        public string? PCloudShareUrl { get; set; }
        public string? MediaFireShareUrl { get; set; }
        public string Status { get; set; } = "Ready";
        public List<RemoteFile> Files { get; set; } = new();
    }

    private void SaveEntries()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(EntriesPath)!);
            // Save in display order (_rows), not dict order, so the layout survives restart.
            var list = _rows.Select(e => new SavedEntry
            {
                NormalizedKey = e.NormalizedKey,
                DisplayName = e.DisplayName,
                IsFolder = e.IsFolder,
                TotalSize = e.TotalSize,
                PrimaryHost = e.PrimaryHost,
                PCloudShareUrl = e.PCloudShareUrl,
                MediaFireShareUrl = e.MediaFireShareUrl,
                Status = e.Status.ToString(),
                Files = e.Files.ToList(),
            }).ToList();
            File.WriteAllText(EntriesPath, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* persistence must never break the app */ }
    }

    private void LoadEntries()
    {
        try
        {
            if (!File.Exists(EntriesPath)) return;
            var list = JsonSerializer.Deserialize<List<SavedEntry>>(File.ReadAllText(EntriesPath));
            if (list == null) return;
            var rebuilt = new List<LeecherEntry>();
            foreach (var s in list)
            {
                // Mid-resolve entries (no files yet) can't download; user re-scans.
                // Dead / NeedsPassword are link states, kept without files.
                if (s.Files.Count == 0 && s.Status != "Dead" && s.Status != "NeedsPassword")
                    continue;
                var entry = new LeecherEntry
                {
                    DisplayName = s.DisplayName,
                    NormalizedKey = s.NormalizedKey,
                    IsFolder = s.IsFolder,
                    FileCount = s.Files.Count,
                    TotalSize = s.TotalSize,
                    PrimaryHost = s.PrimaryHost,
                };
                entry.PCloudShareUrl = s.PCloudShareUrl;
                entry.MediaFireShareUrl = s.MediaFireShareUrl;
                foreach (var f in s.Files) entry.Files.Add(f);
                // An interrupted download becomes Paused (its .part stays on disk, resumes via Range).
                // Passwords are session-only -> protected links re-prompt after restart.
                entry.Status = s.Status switch
                {
                    "Downloading" => EntryStatus.Paused,
                    "Paused" => EntryStatus.Paused,
                    "Completed" => EntryStatus.Completed,
                    "Failed" => EntryStatus.Failed,
                    "Dead" => EntryStatus.Dead,
                    "NeedsPassword" => EntryStatus.NeedsPassword,
                    _ => EntryStatus.Ready,
                };
                entry.StatusText = entry.Status switch
                {
                    EntryStatus.Completed => "Completed",
                    EntryStatus.Failed => "Failed",
                    EntryStatus.Dead => "Link dead",
                    EntryStatus.NeedsPassword => "Password required",
                    EntryStatus.Paused => "Paused",
                    _ => "Ready",
                };
                // Ground truth for progress is the bytes actually on disk, not the saved counter.
                entry.DownloadedBytes = ExistingPartBytes(entry);
                entry.Progress = entry.TotalSize > 0
                    ? Math.Min(100, entry.DownloadedBytes * 100.0 / entry.TotalSize) : 0;
                entry.RefreshDerived();
                rebuilt.Add(entry);
            }
            // Completed first (stable), then the rest — matches the pre-close layout (user spec 2026-10-16).
            // Add directly in order (bypasses SyncRows' dict ordering).
            foreach (var entry in rebuilt.OrderByDescending(e => e.Status == EntryStatus.Completed))
            {
                _entries[entry.NormalizedKey] = entry;
                _byId[entry.Id] = entry;
                _rows.Add(entry);
            }
            UpdateCounts();
        }
        catch { /* a corrupt file must never break startup */ }
    }

    private static long ExistingPartBytes(LeecherEntry entry)
    {
        try
        {
            string targetDir = DownloadEngine.DownloadRoot;
            if (entry.IsFolder) targetDir = Path.Combine(targetDir, DownloadEngine.Sanitize(entry.DisplayName));
            long sum = 0;
            foreach (var f in entry.Files)
            {
                string basePath = Path.Combine(targetDir, DownloadEngine.Sanitize(f.Name));
                if (File.Exists(basePath + ".part")) sum += new FileInfo(basePath + ".part").Length;
                else if (File.Exists(basePath)) sum += new FileInfo(basePath).Length;
            }
            return sum;
        }
        catch { return 0; }
    }

    protected override void OnClosed(EventArgs e)
    {
        _scanCts?.Cancel();
        foreach (var cts in _dlCts.Values) cts.Cancel();
        _syncTimer.Stop();
        base.OnClosed(e);
    }
}
