using System.Windows;
using VisionCanvas.Controls;

namespace VisionCanvas.MvvmSample.Views;

// View는 화면 수명만 다룬다. 모델을 만드는 일과 불러오는 명령은 App/VM에 있다.
public partial class InspectionModelWindow : Window
{
    public InspectionModelWindow() => InitializeComponent();
    public Dx12Viewport FirstViewport => FirstEditor.Viewport;
    public Dx12Viewport SecondViewport => SecondEditor.Viewport;
    private void OnClosed(object? sender, EventArgs args)
    {
        FirstEditor.Dispose();
        SecondEditor.Dispose();
    }
}
