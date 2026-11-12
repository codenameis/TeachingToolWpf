using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using VisionCanvas.Controls;
using VisionCanvas.Presentation;
using VisionCanvas.SampleShell.ViewModels;

namespace VisionCanvas.SampleShell.Views;

/// <summary>
/// 샘플 앱이 공유하는 편집 창 크롬(툴바·내비게이션·정렬 패널·상태줄·단축키)을 제공한다.
/// 파생 창은 생성자에서 백엔드 <see cref="ViewportControlBase"/>만 전달한다.
/// </summary>
public abstract class SampleEditorWindowBase : Window
{
    private const double DefaultWindowWidth = 1280.0;
    private const double DefaultWindowHeight = 900.0;
    private const double MinimumWindowWidth = 900.0;
    private const double MinimumWindowHeight = 600.0;
    private const double DefaultFontSize = 13.0;
    private static readonly Color WindowBackgroundColor = Color.FromRgb(0xF4, 0xF5, 0xF7);

    private readonly ViewportControlBase _viewport;

    protected SampleEditorWindowBase(string title, ViewportControlBase viewport)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(viewport);

        Title = title;
        Width = DefaultWindowWidth;
        Height = DefaultWindowHeight;
        MinWidth = MinimumWindowWidth;
        MinHeight = MinimumWindowHeight;
        Background = new SolidColorBrush(WindowBackgroundColor);
        FontFamily = new FontFamily("Malgun Gothic");
        FontSize = DefaultFontSize;

        _viewport = viewport;
        _viewport.SetBinding(ViewportControlBase.SceneProperty, new Binding(nameof(EditorViewModelBase.Scene)));
        _viewport.SetBinding(ViewportControlBase.PointerInputProperty, new Binding(nameof(EditorViewModelBase.Input)));
        _viewport.SetBinding(ViewportControlBase.LayoutStateProperty, new Binding(nameof(EditorViewModelBase.Layout)));
        _viewport.SetBinding(ViewportControlBase.KeyActionProperty, new Binding(nameof(EditorViewModelBase.KeyAction)));
        _viewport.SetBinding(ViewportControlBase.IsInputEnabledProperty, new Binding(nameof(EditorViewModelBase.IsNavigationEnabled)));

        var chrome = new SampleEditorChrome
        {
            ViewportContent = _viewport
        };
        Content = chrome;

        _viewport.InputFailed += ReportInputFailure;
        _viewport.RendererStatusChanged += ReportRendererStatus;
        _viewport.Tiles.Failed += ReportTileFailure;
        PreviewKeyDown += OnPreviewKeyDown;
        Closed += OnClosed;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs args)
    {
        if (!ViewportKeyboard.IsControlInput(Keyboard.FocusedElement)
            && !Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)
            && DataContext is EditorViewModelBase model)
        {
            args.Handled = model.HandleKey(
                args.Key,
                Keyboard.Modifiers.HasFlag(ModifierKeys.Control),
                Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        }
    }

    private void ReportInputFailure(Exception error) => (DataContext as EditorViewModelBase)?.ReportError(error);

    private void ReportRendererStatus(string message) => (DataContext as EditorViewModelBase)?.ReportRenderer(message);

    private void ReportTileFailure(string message)
        => Dispatcher.BeginInvoke(() => (DataContext as EditorViewModelBase)?.ReportStatus(message));

    private void OnClosed(object? sender, EventArgs args)
    {
        PreviewKeyDown -= OnPreviewKeyDown;
        Closed -= OnClosed;
        _viewport.InputFailed -= ReportInputFailure;
        _viewport.RendererStatusChanged -= ReportRendererStatus;
        _viewport.Tiles.Failed -= ReportTileFailure;
        _viewport.Dispose();
    }
}
