using System.Windows;
using System.Windows.Input;
using VisionCanvas.Vulkan.Controls;
using VisionCanvas.VulkanSample.ViewModels;
using VisionCanvas.Presentation;

namespace VisionCanvas.VulkanSample.Views;

public partial class EditorWindow : Window
{
    public EditorWindow()
    {
        InitializeComponent();
        Canvas.InputFailed += ReportInputFailure;
        Canvas.RendererStatusChanged += ReportRendererStatus;
        Canvas.Tiles.Failed += ReportTileFailure;
    }

    public VulkanViewport Viewport => Canvas;

    private void OnPreviewKeyDown(object sender, KeyEventArgs args)
    {
        if (!ViewportKeyboard.IsControlInput(Keyboard.FocusedElement) && !Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) && DataContext is EditorViewModel model)
        {
            args.Handled = model.HandleKey(args.Key, Keyboard.Modifiers.HasFlag(ModifierKeys.Control), Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        }
    }

    private void ReportInputFailure(Exception error) => (DataContext as EditorViewModel)?.ReportError(error);
    private void ReportRendererStatus(string message) => (DataContext as EditorViewModel)?.ReportRenderer(message);
    private void ReportTileFailure(string message) => Dispatcher.BeginInvoke(() => (DataContext as EditorViewModel)?.ReportStatus(message));

    private void OnClosed(object? sender, EventArgs args)
    {
        Canvas.InputFailed -= ReportInputFailure;
        Canvas.RendererStatusChanged -= ReportRendererStatus;
        Canvas.Tiles.Failed -= ReportTileFailure;
        Canvas.Dispose();
    }
}
