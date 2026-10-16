using System.IO;
using System.Windows;
using AnomalyMonitor.Services;
using AnomalyMonitor.ViewModels;
using AnomalyMonitor.Views;
using VisionAnomaly.Monitoring;

namespace AnomalyMonitor;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var window = new MainWindow();
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WatchAnomalyMonitor", "Events");
        var store = new AlarmStore(directory);
        var featurePath = Path.Combine(AppContext.BaseDirectory, "Models", "resnet18-patches.onnx");
        var sessions = Enumerable.Range(1, 4).Select(index => new CameraSession($"CAM {index:00}", featurePath, store)).ToArray();
        void Dispatch(Action action)
        {
            if (!Dispatcher.HasShutdownStarted)
            {
                Dispatcher.BeginInvoke(action);
            }
        }
        var model = new DashboardViewModel(sessions, store, new MonitorFiles(window), Dispatch);
        window.DataContext = model;
        var links = new List<MonitorLink>();
        for (var index = 0; index < sessions.Length; index++)
        {
            links.Add(new(sessions[index], window.Monitors[index], model.Cameras[index], Dispatch));
        }

        var closing = false;
        var closed = false;
        window.Closing += async (_, args) =>
        {
            if (closed)
            {
                return;
            }

            args.Cancel = true;
            if (closing)
            {
                return;
            }

            closing = true;
            window.IsEnabled = false;
            model.Message = "영상 수집과 검사 작업을 종료하고 있습니다…";
            await Task.WhenAll(sessions.Select(async session => await session.DisposeAsync()));
            foreach (var link in links)
            {
                link.Dispose();
            }

            model.Dispose();
            foreach (var monitor in window.Monitors)
            {
                monitor.Dispose();
            }

            closed = true;
            window.Close();
        };
        MainWindow = window;
        window.Show();
    }
}
