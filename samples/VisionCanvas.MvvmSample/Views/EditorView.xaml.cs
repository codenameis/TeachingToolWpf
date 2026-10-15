using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using VisionCanvas.Controls;
using VisionCanvas.MvvmSample.ViewModels;

namespace VisionCanvas.MvvmSample.Views;

// XAML에서 하기 어려운 UI 연결만 맡는다. ROI 생성·삭제 같은 편집 규칙은 VM/Core에 있다.
public partial class EditorView : UserControl, IDisposable
{
    public EditorView()
    {
        // XAML에 적힌 버튼과 Canvas를 실제 객체로 만든다. 이 호출 뒤 Canvas 필드를 사용할 수 있다.
        InitializeComponent();
        Canvas.InputFailed += ReportInputFailure;
        Canvas.Tiles.Failed += ReportTileFailure;
        Canvas.RendererStatusChanged += ReportRendererStatus;
    }

    // App이 이미지 표시 서비스를 연결할 때 사용한다. VM에는 이 구체 컨트롤을 넘기지 않는다.
    public Dx12Viewport Viewport => Canvas;

    private void OnPreviewKeyDown(object sender, KeyEventArgs args)
    {
        // 글자 입력칸의 Delete/Ctrl+Z 등을 ROI 명령으로 가로채지 않도록 먼저 확인한다.
        if (Keyboard.FocusedElement is TextBoxBase or RangeBase)
        {
            return;
        }
        if (DataContext is EditorViewModel model)
        {
            // Handled=true이면 이 키를 처리했다는 뜻이다. 다른 UI가 같은 입력을 다시 처리하지 않게 한다.
            args.Handled = model.HandleKey(args.Key, Keyboard.Modifiers.HasFlag(ModifierKeys.Control),
                Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        }
    }

    private void ReportInputFailure(Exception error) => (DataContext as EditorViewModel)?.ReportError(error);
    private void ReportRendererStatus(string status) => (DataContext as EditorViewModel)?.ReportStatus(status);
    // 타일(이미지 조각)은 작업 스레드에서 읽는다. 화면에 알릴 때는 Dispatcher로 UI 스레드에 넘긴다.
    // BeginInvoke는 UI가 처리할 일을 예약하며, 현재 작업 스레드에서 UI 처리가 끝나기를 기다리지 않는다.
    private void ReportTileFailure(string message) => Dispatcher.BeginInvoke(() =>
        (DataContext as EditorViewModel)?.ReportStatus($"이미지 읽기 오류: {message}"));

    public void Dispose()
    {
        // +=로 연결한 이벤트는 수명이 끝날 때 -=로 해제한다.
        // 창이 닫힌 뒤에도 콜백이 실행되거나 이벤트 연결 때문에 창이 메모리에 남는 것을 막는다.
        Canvas.InputFailed -= ReportInputFailure;
        Canvas.Tiles.Failed -= ReportTileFailure;
        Canvas.RendererStatusChanged -= ReportRendererStatus;
        Canvas.Dispose();
    }
}
