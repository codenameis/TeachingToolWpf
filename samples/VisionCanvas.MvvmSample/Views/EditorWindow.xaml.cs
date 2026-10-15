using System.Windows;
using VisionCanvas.Controls;

namespace VisionCanvas.MvvmSample.Views;

// 단일 카메라를 별도 창에 넣고 싶을 때 쓰는 얇은 호스트다. 편집 UI는 EditorView와 공유한다.
public partial class EditorWindow : Window
{
    public EditorWindow() => InitializeComponent();
    public Dx12Viewport Viewport => Editor.Viewport;
    private void OnClosed(object? sender, EventArgs args) => Editor.Dispose();
}
