using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PhoenixLeecher;

public sealed class BytesConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not long b) return "—";
        if (b < 0) return "—";
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double d = b; int i = 0;
        while (d >= 1024 && i < u.Length - 1) { d /= 1024; i++; }
        return $"{d:F1} {u[i]}";
    }
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class SpeedConverter : IValueConverter
{
    private static readonly BytesConverter B = new();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not double s || s <= 0) return "—";
        return B.Convert((long)s, targetType, parameter, culture) + "/s";
    }
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class SizePairConverter : IMultiValueConverter
{
    private static readonly BytesConverter B = new();
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        long done = values[0] is long d ? d : 0;
        long total = values[1] is long t ? t : 0;
        string ts = (string)B.Convert(total, targetType, parameter, culture)!;
        if (done <= 0 || done >= total) return ts;
        return $"{B.Convert(done, targetType, parameter, culture)} / {ts}";
    }
    public object[] ConvertBack(object value, Type[] t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class ElapsedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not TimeSpan ts || ts == TimeSpan.Zero) return "—";
        return ts.ToString(@"hh\:mm\:ss");
    }
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class StatusToBarBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not Models.EntryStatus s) return Application.Current.Resources["AccentBrush"];
        return s switch
        {
            Models.EntryStatus.Completed => Application.Current.Resources["DoneBrush"],
            Models.EntryStatus.Paused => Application.Current.Resources["PausedBrush"],
            Models.EntryStatus.Failed or Models.EntryStatus.Dead => Application.Current.Resources["DangerBrush"],
            _ => Application.Current.Resources["AccentBrush"],
        };
    }
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}
