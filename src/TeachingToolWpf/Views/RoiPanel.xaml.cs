using System.Windows.Controls;

namespace TeachingToolWpf.Views;

public partial class RoiPanel : UserControl
{
    public RoiPanel() => InitializeComponent();

    public Controls.RoiListBox List => RoiList;
}
