using System.ComponentModel;
using System.Windows;
using AnomalyMonitor.ViewModels;
using VisionAnomaly.Controls;

namespace AnomalyMonitor.Views;

public partial class MainWindow : Window
{
    private DashboardViewModel? _model;
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_model is not null)
            {
                _model.PropertyChanged -= OnModelChanged;
            }

            _model = DataContext as DashboardViewModel;
            if (_model is not null)
            {
                _model.PropertyChanged += OnModelChanged;
            }
        };
        Closed += (_, _) => { if (_model is not null) { _model.PropertyChanged -= OnModelChanged; } };
    }
    public IReadOnlyList<CameraMonitorControl> Monitors => [FirstCamera, SecondCamera, ThirdCamera, FourthCamera];
    private void OnModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(DashboardViewModel.SingleView))
        {
            CameraGrid.Columns = CameraGrid.Rows = _model!.SingleView ? 1 : 2;
        }

        if (args.PropertyName == nameof(DashboardViewModel.SelectedAlarm) && _model!.SelectedAlarm is not null)
        {
            EventsTab.IsSelected = true;
        }
    }
}
