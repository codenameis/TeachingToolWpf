// File: Views/Fluent2StudioWindow.xaml.cs
using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using TeachingToolWpf.Controls;
using TeachingToolWpf.Fluent2.ViewModels;
using TeachingToolWpf.ViewModels;
using TeachingToolWpf.Views;
using VisionCanvas.Presentation;
using Wpf.Ui.Controls;

namespace TeachingToolWpf.Fluent2.Views;

public sealed partial class Fluent2StudioWindow : FluentWindow
{
    private const int LiveFilterRefreshIntervalMs = 200; // [Domain Why]: 5 Hz live filter preview refresh rate per ADR-038/ADR-039.
    private PatternMaskWindow? _patternMaskWindow;

    public Fluent2StudioWindow()
    {
        InitializeComponent();
    }

    internal void Bind(Fluent2StudioViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        MainWindowViewModel studio = viewModel.Studio;

        studio.PatternMaskEditor.AttachFrameRetainer(Viewport.RetainFrame);
        CrosshairLayerLink guideLink = new(Viewport, studio.GuideSettings);
        RoiListSelectionLink selectionLink = new(RoiList, studio);
        DispatcherTimer filterTimer = new(
            TimeSpan.FromMilliseconds(LiveFilterRefreshIntervalMs),
            DispatcherPriority.Background,
            (_, _) => studio.RefreshLivePreview(),
            Dispatcher);

        void ReportLiveFailure(Exception error)
        {
            Dispatcher.BeginInvoke(() => studio.ReportError(error));
        }

        void ReportTileFailure(string message)
        {
            Dispatcher.BeginInvoke(() => studio.Status = message);
        }

        void ReportRendererStatus(string status)
        {
            studio.GpuStatus = status;
        }

        void OpenPatternMaskEditor()
        {
            _ = OpenPatternMaskEditorAsync(studio);
        }

        studio.Live.Failed += ReportLiveFailure;
        studio.PatternMaskWindowRequested += OpenPatternMaskEditor;
        Viewport.InputFailed += studio.ReportError;
        Viewport.OverlayPresented += studio.Matching.RecordOverlayPresentation;
        Viewport.RendererStatusChanged += ReportRendererStatus;
        Viewport.MetricsChanged += studio.UpdatePerformance;
        Viewport.Tiles.Failed += ReportTileFailure;

        PreviewKeyDown += (_, args) =>
        {
            if (ViewportKeyboard.IsControlInput(Keyboard.FocusedElement) || Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
            {
                return;
            }

            args.Handled = studio.HandleKey(
                args.Key,
                Keyboard.Modifiers.HasFlag(ModifierKeys.Control),
                Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        };

        Closing += (_, _) => filterTimer.Stop();
        Closed += (_, _) =>
        {
            studio.Live.Failed -= ReportLiveFailure;
            studio.PatternMaskWindowRequested -= OpenPatternMaskEditor;
            Viewport.InputFailed -= studio.ReportError;
            Viewport.OverlayPresented -= studio.Matching.RecordOverlayPresentation;
            Viewport.RendererStatusChanged -= ReportRendererStatus;
            Viewport.MetricsChanged -= studio.UpdatePerformance;
            Viewport.Tiles.Failed -= ReportTileFailure;
            selectionLink.Dispose();
            guideLink.Dispose();
            viewModel.Dispose();
        };
    }

    private async Task OpenPatternMaskEditorAsync(MainWindowViewModel studio)
    {
        await studio.RefreshPatternMaskEditorAsync();
        if (_patternMaskWindow is null)
        {
            _patternMaskWindow = new PatternMaskWindow
            {
                Owner = this,
                DataContext = studio.PatternMaskEditor
            };
            _patternMaskWindow.Closed += (_, _) => _patternMaskWindow = null;
            _patternMaskWindow.Show();
        }
        else
        {
            if (_patternMaskWindow.WindowState == WindowState.Minimized)
            {
                _patternMaskWindow.WindowState = WindowState.Normal;
            }

            _patternMaskWindow.Activate();
        }
    }
}
