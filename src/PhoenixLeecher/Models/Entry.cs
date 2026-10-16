using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PhoenixLeecher.Models;

public enum EntryStatus
{
    Ready,          // resolved, waiting for user to press Download
    Resolving,      // inside-name/size being resolved
    NeedsPassword,  // password-protected share link
    Downloading,
    Paused,
    Completed,
    Failed,
    Dead,           // share link dead after retries
}

public sealed class RemoteFile
{
    public string Name { get; set; } = "";
    public long Size { get; set; }
    // pCloud: fileid ; MediaFire: quickkey
    public string RefId { get; set; } = "";
}

public sealed class LeecherEntry : INotifyPropertyChanged
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public string NormalizedKey { get; set; } = "";

    private string _displayName = "";
    public string DisplayName { get => _displayName; set => Set(ref _displayName, value); }

    public bool IsFolder { get; set; }
    public int FileCount { get; set; }
    public long TotalSize { get; set; }

    private int _rowNumber;
    public int RowNumber { get => _rowNumber; set => Set(ref _rowNumber, value); } // 1-based display order

    // "pcloud" | "mediafire" — which host is primary for download
    public string PrimaryHost { get; set; } = "pcloud";
    // Share URLs (inside-name resolved from these). MediaFire kept as backup.
    public string? PCloudShareUrl { get; set; }
    public string? MediaFireShareUrl { get; set; }
    public bool HasBackup => PCloudShareUrl != null && MediaFireShareUrl != null;

    // Session-only. Never persisted.
    public string? Password { get; set; }

    // Set by the downloader when pCloud failed AND the MediaFire backup also failed.
    // Session-only. Never persisted.
    public bool BothLinksFailed { get; set; }

    public List<RemoteFile> Files { get; } = new();

    private EntryStatus _status = EntryStatus.Resolving;
    public EntryStatus Status { get => _status; set => Set(ref _status, value); }

    private double _progress;
    public double Progress { get => _progress; set => Set(ref _progress, value); } // 0..100

    private long _downloadedBytes;
    public long DownloadedBytes { get => _downloadedBytes; set => Set(ref _downloadedBytes, value); }

    private double _speedBps;
    public double SpeedBps { get => _speedBps; set => Set(ref _speedBps, value); }

    private TimeSpan _elapsed;
    public TimeSpan Elapsed { get => _elapsed; set => Set(ref _elapsed, value); }

    private string _statusText = "";
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }

    public string DedupNote =>
        HasBackup ? "also on " + (PrimaryHost == "pcloud" ? "MediaFire" : "pCloud") + " — backup if " + PrimaryHost + " fails" : "";

    public event PropertyChangedEventHandler? PropertyChanged;
    public void RefreshDerived()
    {
        RaisePropertyChanged(nameof(DedupNote));
        RaisePropertyChanged(nameof(HasBackup));
    }
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return;
        field = value;
        RaisePropertyChanged(name);
    }
    // Entries are mutated from background threads (scan/resolve); WPF bindings
    // must be notified on the UI thread or the app throws a cross-thread exception.
    private void RaisePropertyChanged(string? name)
    {
        var handler = PropertyChanged;
        if (handler == null) return;
        var disp = System.Windows.Application.Current?.Dispatcher;
        if (disp != null && !disp.CheckAccess())
            disp.BeginInvoke(() => handler(this, new PropertyChangedEventArgs(name)));
        else
            handler(this, new PropertyChangedEventArgs(name));
    }
}
