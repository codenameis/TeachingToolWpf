using System.Configuration;
using System.Data;
using System.Windows;

namespace TeachingToolWpf;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Contains("--repeated-roi-benchmark"))
        {
            StartupUri = new Uri("Views/RepeatedRoiWindow.xaml", UriKind.Relative);
        }
        base.OnStartup(e);
    }
}
