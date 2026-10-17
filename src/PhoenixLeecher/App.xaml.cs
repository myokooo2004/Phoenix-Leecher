using System.IO;
using System.Threading.Tasks;
using System.Windows;

namespace PhoenixLeecher;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Crash logging (diagnostic 2026-10-17): write unhandled exceptions to
        // %AppData%\PhoenixLeecher\crash.log for root-cause analysis.
        AppDomain.CurrentDomain.UnhandledException += (s, a) =>
            LogCrash("UnhandledException", a.ExceptionObject as Exception);
        DispatcherUnhandledException += (s, a) =>
        {
            LogCrash("DispatcherUnhandledException", a.Exception);
            a.Handled = true; // don't crash the app; log and continue
        };
        TaskScheduler.UnobservedTaskException += (s, a) =>
        {
            LogCrash("UnobservedTaskException", a.Exception);
            a.SetObserved();
        };
        // Default theme: light (user spec). No bundled fonts: WPF uses Segoe UI,
        // Myanmar glyphs fall back automatically to built-in "Myanmar Text".
        ApplyTheme("Light");
    }

    private static void LogCrash(string source, Exception? ex)
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "PhoenixLeecher");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "crash.log");
            string text = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}\n" +
                          $"{ex?.ToString() ?? "(no exception object)"}\n" +
                          new string('-', 60) + "\n";
            File.AppendAllText(path, text);
        }
        catch { /* logging must never crash */ }
    }

    public static void ApplyTheme(string name)
    {
        var dict = new ResourceDictionary
        {
            Source = new Uri($"/Themes/{name}.xaml", UriKind.Relative)
        };
        var app = Current;
        // Keep first dictionary (app-level strings) if any; replace theme dict.
        for (int i = app.Resources.MergedDictionaries.Count - 1; i >= 0; i--)
        {
            var src = app.Resources.MergedDictionaries[i].Source?.ToString() ?? "";
            if (src.Contains("/Themes/"))
                app.Resources.MergedDictionaries.RemoveAt(i);
        }
        app.Resources.MergedDictionaries.Add(dict);
        ThemeName = name;
    }

    public static string ThemeName { get; private set; } = "Light";
}
