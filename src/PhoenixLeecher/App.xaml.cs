using System.Windows;

namespace PhoenixLeecher;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Default theme: light (user spec). No bundled fonts: WPF uses Segoe UI,
        // Myanmar glyphs fall back automatically to built-in "Myanmar Text".
        ApplyTheme("Light");
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
