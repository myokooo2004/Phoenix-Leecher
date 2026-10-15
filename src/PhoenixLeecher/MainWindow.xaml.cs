using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
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
        UpdateCounts();
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
                ScanBtnLabel.Text = "Scan";
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
        if (string.IsNullOrWhiteSpace(CurrentUrl)) return;
        var links = await Task.Run(() => _scanner.FindLinksAsync(CurrentUrl, CancellationToken.None));
        await Task.Run(async () =>
        {
            foreach (var link in links)
                await _scanner.ResolveAndAddAsync(link, _entries, null, CancellationToken.None);
        });
        SyncRows();
    }

    private async void FindLinks_Click(object sender, RoutedEventArgs e)
    {
        ScanBtn.IsEnabled = false;
        try { await DoFindLinksAsync(); }
        catch (Exception ex)
        {
            MessageBox.Show("Find links failed: " + ex.Message, "Phoenix Leecher",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { ScanBtn.IsEnabled = true; }
    }

    // ---------- Scan (older posts, 50 links, resumable) ----------
    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(CurrentUrl)) return;
        ScanBtn.IsEnabled = false;
        try
        {
            // Page links first (immediate), then older-posts crawl.
            await DoFindLinksAsync();

            _scanCts = new CancellationTokenSource();
            StopBtn.IsEnabled = true;
            ScanStrip.Visibility = Visibility.Visible;
            await Task.Run(() => _scanner.ScanAsync(CurrentUrl, _entries, _scanProgress, _scanCts.Token));
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
            _scanCts?.Dispose();
            _scanCts = null;
            ScanBtn.IsEnabled = true;
        }
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
        var list = _rows.Where(x => x.Status is EntryStatus.Ready or EntryStatus.Paused or EntryStatus.Failed).ToList();
        await Task.Run(() =>
        {
            foreach (var entry in list)
            {
                Dispatcher.Invoke(() => StartDownload(entry));
                // Wait until this entry leaves Downloading (sequential entries; files inside use 3 slots).
                while (_dlCts.ContainsKey(entry.Id)) Thread.Sleep(500);
            }
        });
    }

    private void PauseAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var cts in _dlCts.Values.ToList()) cts.Cancel();
    }

    private void ResumeAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var entry in _rows.Where(x => x.Status is EntryStatus.Paused or EntryStatus.Failed).ToList())
            StartDownload(entry);
    }

    private void RowDelete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not LeecherEntry entry) return;
        if (_dlCts.TryGetValue(entry.Id, out var cts)) cts.Cancel();
        _entries.TryRemove(entry.NormalizedKey, out _);
        SyncRows();
    }

    private void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is LeecherEntry entry) RowDelete_Click(sender, e);
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
            if (url == null) return;
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

    protected override void OnClosed(EventArgs e)
    {
        _scanCts?.Cancel();
        foreach (var cts in _dlCts.Values) cts.Cancel();
        _syncTimer.Stop();
        base.OnClosed(e);
    }
}
