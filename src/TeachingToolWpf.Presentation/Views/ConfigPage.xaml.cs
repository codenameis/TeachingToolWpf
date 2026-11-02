using System.Windows;

namespace TeachingToolWpf.Views;

public partial class ConfigPage : Window
{
    public ConfigPage() => InitializeComponent();

    private void ClosePage(object sender, RoutedEventArgs e) => Close();
}
