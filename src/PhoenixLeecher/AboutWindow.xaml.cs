using System.Reflection;
using System.Windows;

namespace PhoenixLeecher;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $"Version {v?.Major}.{v?.Minor}.{v?.Build}  •  .NET 8  •  Windows x64";
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => Close();
}
