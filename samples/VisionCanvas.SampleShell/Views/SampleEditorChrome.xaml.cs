using System.Windows;
using System.Windows.Controls;

namespace VisionCanvas.SampleShell.Views;

/// <summary>샘플 창이 공통으로 사용하는 상하단 도구 모음·내비게이션·정렬 패널 및 중앙 뷰포트 호스트 컨트롤.</summary>
public sealed partial class SampleEditorChrome : UserControl
{
    public SampleEditorChrome() => InitializeComponent();

    public UIElement? ViewportContent
    {
        get => ViewportHostBorder.Child;
        set => ViewportHostBorder.Child = value;
    }
}
