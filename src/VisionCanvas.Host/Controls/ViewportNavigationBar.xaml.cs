using System.Windows.Controls;
using VisionCanvas.Presentation;

namespace VisionCanvas.Controls;

public partial class ViewportNavigationBar : UserControl
{
    public ViewportNavigationBar() => InitializeComponent();

    private void OnDropDownOpened(object sender, EventArgs args)
        => (DataContext as ViewportNavigationViewModel)?.RefreshTargets();
}
