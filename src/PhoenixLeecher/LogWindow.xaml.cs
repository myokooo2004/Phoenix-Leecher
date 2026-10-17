using System.IO;
using System.Windows;

namespace PhoenixLeecher;

public partial class LogWindow : Window
{
    public LogWindow()
    {
        InitializeComponent();
        LoadLog();
    }

    private void LoadLog()
    {
        try
        {
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "PhoenixLeecher", "crash.log");
            LogText.Text = File.Exists(path)
                ? File.ReadAllText(path)
                : "(No log yet — the log appears here after a crash or error.)";
        }
        catch (Exception ex)
        {
            LogText.Text = "(Could not read log: " + ex.Message + ")";
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(LogText.Text); }
        catch { /* clipboard unavailable */ }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
