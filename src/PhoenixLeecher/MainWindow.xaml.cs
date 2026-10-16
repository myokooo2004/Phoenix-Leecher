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
    private readonly Dictionary<string, CancellationTokenSource> _dlCts = new();
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
            SaveEntries();
        }
        UpdateCounts();
    }

    private void ApplyScanProgress(ScanProgress p)
    {
        ScanBar.Maximum = p.Target;
        ScanBar.Value = p.LinksFound;
        ScanText.Text = p.Running
            ? $"Scanning older posts… {p.LinksFound}/{p.Target} links" +
              (p.TotalPosts > 0 ? $" · post {p.PostsDone}/{p.TotalPosts}" : $" · {p.PostsDone} posts")
            : $"Scan {p.Note}: {p.LinksFound}/{p.Target} links";
        if (!p.Running)
        {
            ScanBtn.IsEnabled = true;
            StopBtn.IsEnabled = false;
            // Target reached or exhausted -> clear saved state; stopped early -> keep for Resume.
            if (p.LinksFound >= p.Target || p.Note.Contains("exhaust"))
            {
                _scanner.ClearState(UrlBox.Text.Trim());
                ScanBtnLabel.Text = "Whole scan";
            }
            else if (LinkScanner.IsBlogspot(UrlBox.Text.Trim()))
            {
                ScanBtnLabel.Text = $"Resume scan ({p.LinksFound}/{p.Target})";
            }
        }
    }

    private string CurrentUrl => UrlBox.Text.Trim();

    // ---------- Find Links (immediate, page only) ----------
    private async Task DoFindLinksAsync()
    {
        string url = CurrentUrl; // capture on UI thread: UrlBox cannot be read from a worker thread
        if (string.IsNullOrWhiteSpace(url)) return;
        var links = await Task.Run(() => _scanner.FindLinksAsync(url, CancellationToken.None));
        await Task.Run(async () =>
        {
            foreach (var link in links)
                await _scanner.ResolveAndAddAsync(link, _entries, null, CancellationToken.None);
        });
        SyncRows();
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

    // ---------- Whole scan (page + older posts, 50 links, resumable) ----------
    private async void WholeScan_Click(object sender, RoutedEventArgs e)
    {
        if (_scanRunning) return;
        string url = CurrentUrl; // capture on UI thread: UrlBox cannot be read from a worker thread
        if (string.IsNullOrWhiteSpace(url)) return;
        SetScanRunning(true);
        try
        {
            // Resume: crawl only from the saved cursor (no page re-scrape).
            // Fresh: page links first (immediate), then older-posts crawl.
            var saved = _scanner.LoadState(url);
            bool isResume = saved != null && saved.LinksFound < LinkScanner.LinkTarget
                && LinkScanner.IsBlogspot(url);
            if (!isResume)
                await DoFindLinksAsync();

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
    private void StartDownload(LeecherEntry entry)
    {
        if (entry.Status == EntryStatus.Downloading) return;
        if (entry.Status == EntryStatus.NeedsPassword && string.IsNullOrEmpty(entry.Password))
        {
            MessageBox.Show("This link needs a password — enter it in the row and press Unlock.",
                "Phoenix Leecher", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_dlCts.ContainsKey(entry.Id)) return;
        var cts = new CancellationTokenSource();
        _dlCts[entry.Id] = cts;
        _ = _engine.DownloadAsync(entry, _tickProgress, cts.Token);
    }

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
        var list = _rows.Where(x => x.Status is EntryStatus.Ready or EntryStatus.Paused or EntryStatus.Failed).ToList();
        await Task.Run(() =>
        {
            foreach (var entry in list)
            {
                if (batchCts.Token.IsCancellationRequested) break;
                Dispatcher.Invoke(() => StartDownload(entry));
                // Wait until this entry leaves Downloading (sequential entries; files inside use 3 slots).
                while (_dlCts.ContainsKey(entry.Id))
                {
                    if (batchCts.Token.IsCancellationRequested) break;
                    Thread.Sleep(500);
                }
            }
        });
        if (_dlBatchCts == batchCts) { _dlBatchCts = null; batchCts.Dispose(); }
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
            var fresh = await Task.Run(() => _scanner.ResolveAndAddAsync(url, _entries, pw, CancellationToken.None));
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
            var list = _entries.Values.Select(e => new SavedEntry
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
                _entries[entry.NormalizedKey] = entry;
            }
            SyncRows();
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
