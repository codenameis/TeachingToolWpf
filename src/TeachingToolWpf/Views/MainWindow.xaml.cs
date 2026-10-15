using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using TeachingToolWpf.ViewModels;

namespace TeachingToolWpf.Views;

public partial class MainWindow : Window
{
    private ConfigPage? _configPage;

    public MainWindow() => InitializeComponent();

    // App이 만든 VM을 실제 컨트롤/Dispatcher에 연결한다. 서비스 생성은 App이 소유한다.
    internal void Bind(MainWindowViewModel model)
    {
        DataContext = model;
        var guideLink = new Controls.CrosshairLayerLink(Viewport, model.GuideSettings);
        var selectionLink = new Controls.RoiListSelectionLink(Regions.List, model);
        var filterTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background,
            (_, _) => model.RefreshLivePreview(), Dispatcher);
        void ReportLiveFailure(Exception error) => Dispatcher.BeginInvoke(() => model.Fail(error));
        void ReportTileFailure(string message) => Dispatcher.BeginInvoke(() => model.Status = message);
        void ReportRendererStatus(string status) => model.GpuStatus = status;

        model.Live.Failed += ReportLiveFailure;
        Viewport.InputFailed += model.Fail;
        Viewport.OverlayPresented += model.Matching.RecordOverlayPresentation;
        Viewport.RendererStatusChanged += ReportRendererStatus;
        Viewport.MetricsChanged += model.UpdatePerformance;
        Viewport.Tiles.Failed += ReportTileFailure;
        PreviewKeyDown += (_, args) =>
        {
            if (Keyboard.FocusedElement is TextBoxBase or RangeBase || args.OriginalSource is TextBox or RangeBase)
            {
                return;
            }
            args.Handled = model.HandleKey(args.Key, Keyboard.Modifiers.HasFlag(ModifierKeys.Control), Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        };
        Closing += (_, _) => filterTimer.Stop();
        Closed += (_, _) =>
        {
            model.Live.Failed -= ReportLiveFailure;
            Viewport.InputFailed -= model.Fail;
            Viewport.OverlayPresented -= model.Matching.RecordOverlayPresentation;
            Viewport.RendererStatusChanged -= ReportRendererStatus;
            Viewport.MetricsChanged -= model.UpdatePerformance;
            Viewport.Tiles.Failed -= ReportTileFailure;
            selectionLink.Dispose();
            guideLink.Dispose();
        };
    }

    private void OpenConfiguration(object sender, RoutedEventArgs e)
    {
        if (_configPage is null)
        {
            _configPage = new()
            {
                Owner = this,
                DataContext = DataContext
            };
            _configPage.Closed += (_, _) => _configPage = null;
            _configPage.Show();
        }
        else
        {
            if (_configPage.WindowState == WindowState.Minimized)
            {
                _configPage.WindowState = WindowState.Normal;
            }
            _configPage.Activate();
        }
    }
}
