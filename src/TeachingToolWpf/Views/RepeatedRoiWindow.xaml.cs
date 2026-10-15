using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using TeachingToolWpf.ViewModels;

namespace TeachingToolWpf.Views;

public partial class RepeatedRoiWindow : Window
{
    public RepeatedRoiWindow() => InitializeComponent();

    internal void Bind(RepeatedRoiViewModel model)
    {
        DataContext = model;
        void ReportTileFailure(string message) => Dispatcher.BeginInvoke(() => model.Status = message);
        Viewport.InputFailed += model.Fail;
        Viewport.Tiles.Failed += ReportTileFailure;
        Viewport.RendererStatusChanged += model.UpdateRendererStatus;
        Viewport.MetricsChanged += model.UpdatePerformance;
        PreviewKeyDown += (_, args) =>
        {
            if (Keyboard.FocusedElement is not TextBoxBase)
            {
                args.Handled = model.HandleKey(args.Key, Keyboard.Modifiers.HasFlag(ModifierKeys.Control), Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
            }
        };
        Closed += (_, _) =>
        {
            Viewport.InputFailed -= model.Fail;
            Viewport.Tiles.Failed -= ReportTileFailure;
            Viewport.RendererStatusChanged -= model.UpdateRendererStatus;
            Viewport.MetricsChanged -= model.UpdatePerformance;
        };
    }
}
