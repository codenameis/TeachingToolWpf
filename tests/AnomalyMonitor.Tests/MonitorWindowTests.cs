using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AnomalyMonitor.Services;
using AnomalyMonitor.ViewModels;
using AnomalyMonitor.Views;
using VisionAnomaly;
using VisionAnomaly.Monitoring;
using VisionCanvas.Rendering;

namespace AnomalyMonitor.Tests;

public sealed class MonitorWindowTests
{
    [Fact]
    public Task RealWindowTrainsFromVideoDetectsAlarmsAndPersistsReview() => StaTest.Run(async () =>
    {
        var output = Path.Combine(StaTest.Root, "artifacts", "anomaly-monitor", "wpf", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        using var errors = new StringWriter();
        using var listener = new TextWriterTraceListener(errors);
        var trace = PresentationTraceSources.DataBindingSource;
        var previous = trace.Switch.Level;
        trace.Switch.Level = SourceLevels.Warning;
        trace.Listeners.Add(listener);
        var window = new MainWindow { ShowActivated = false, ShowInTaskbar = false, Left = -32000, Top = -32000 };
        var store = new AlarmStore(Path.Combine(output, "events"));
        var featurePath = Path.Combine(AppContext.BaseDirectory, "Models", "resnet18-patches.onnx");
        var sessions = Enumerable.Range(1, 4).Select(index => new CameraSession($"CAM {index:00}", featurePath, store)).ToArray();
        var files = new TestFiles { Video = Path.Combine(StaTest.Root, "samples", "AnomalyMonitor.Data", "normal.avi"), Model = Path.Combine(output, "normal.json") };
        void Dispatch(Action action) => window.Dispatcher.BeginInvoke(action);
        using var model = new DashboardViewModel(sessions, store, files, Dispatch);
        window.DataContext = model;
        var links = sessions.Select((session, index) => new MonitorLink(session, window.Monitors[index], model.Cameras[index], Dispatch)).ToArray();
        var camera = model.Cameras[0];
        try
        {
            window.Show();
            Assert.False(camera.MonitorCommand.CanExecute(null));
            StaTest.Click(window, "영상 파일 찾아 열기");
            await StaTest.Until(() => camera.HasVideo && window.Monitors[0].DisplayedSequence > 0, "video preview: " + camera.Status);
            SaveWpf(window, Path.Combine(output, "setup.png"));
            camera.SampleCount = "60";
            await camera.LearnCommand.ExecuteAsync();
            await StaTest.Until(() => camera.MonitorCommand.CanExecute(null), "normal training: " + camera.Status);
            await camera.SaveCommand.ExecuteAsync();
            Assert.True(File.Exists(files.Model));
            Assert.Equal(60, NormalModel.Load(files.Model).TrainingFrames);
            var secondCamera = model.Cameras[1];
            await secondCamera.OpenCommand.ExecuteAsync();
            await StaTest.Until(() => secondCamera.HasVideo, "independent second source");
            await secondCamera.LoadCommand.ExecuteAsync();
            await StaTest.Until(() => secondCamera.MonitorCommand.CanExecute(null), "second model");
            await secondCamera.DisconnectCommand.ExecuteAsync();
            Assert.NotNull(sessions[0].Snapshot.ModelId);
            Assert.Null(sessions[1].Snapshot.ModelId);
            camera.RegionX = "12";
            await Task.Delay(350);
            Assert.Equal("12", camera.RegionX); // 진행 중 상태 갱신이 편집 중 숫자를 덮어쓰지 않는다.

            files.Video = Path.Combine(StaTest.Root, "samples", "AnomalyMonitor.Data", "inspection.avi");
            await camera.OpenCommand.ExecuteAsync();
            await StaTest.Until(() => camera.HasVideo, "inspection source");
            Assert.False(camera.MonitorCommand.CanExecute(null));
            await camera.LoadCommand.ExecuteAsync();
            await StaTest.Until(() => camera.MonitorCommand.CanExecute(null), "model loaded");
            await camera.MonitorCommand.ExecuteAsync();
            await StaTest.Until(() => model.Events.Any(item => item.Record.Kind == AnomalyKind.Appearance), "appearance alarm");
            Assert.True(File.Exists(model.Events[0].EvidenceFile));
            await CaptureViewport(window.Monitors[0].Viewport, Path.Combine(output, "video.png"));
            SaveWpf(window, Path.Combine(output, "layout.png"));
            await camera.MonitorCommand.ExecuteAsync();
            await StaTest.Until(() => camera.MonitorCaption == "감시 시작", "pause");
            var eventCount = model.Events.Count;
            await Task.Delay(400);
            Assert.Equal(eventCount, model.Events.Count);
            model.PendingOnly = false;
            model.SelectedAlarm = model.Events[0];
            await model.NormalCommand.ExecuteAsync();
            Assert.Equal(ReviewStatus.Normal, store.LoadRecent().First(item => item.Id == model.Events[0].Record.Id).Review);
            Assert.NotNull(sessions[0].Snapshot.ModelId);
            model.SingleView = true;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.Single(window.Monitors, monitor => monitor.Visibility == Visibility.Visible);
            Assert.True(window.Monitors[0].ActualWidth > 600);
            model.SingleView = false;
            window.Width = 1180;
            window.Height = 760;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.All(window.Monitors, monitor => Assert.True(monitor.ActualWidth > 200 && monitor.ActualHeight > 100));
            foreach (var monitor in window.Monitors)
            {
                var status = StaTest.Descendants(monitor).OfType<TextBlock>().Single(text => text.Text == monitor.StatusText);
                var position = status.TransformToAncestor(monitor).Transform(new Point(0, 0));
                Assert.True(position.Y + status.ActualHeight <= monitor.RenderSize.Height, "작은 창에서도 영상 상태가 잘리지 않아야 합니다.");
            }
            SaveWpf(window, Path.Combine(output, "compact.png"));
            camera.Threshold = "숫자 아님";
            await camera.ThresholdCommand.ExecuteAsync();
            await Task.Delay(400);
            Assert.Contains("실행 불가", camera.Status);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.Equal("", errors.ToString());
            File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new
            {
                result = "PASS",
                events = model.Events.Select(item => item.Record),
                camera.Renderer,
                bindingErrors = errors.ToString(),
                source = files.Video
            }));
            File.WriteAllText(Path.Combine(StaTest.Root, "artifacts", "anomaly-monitor", "latest-wpf.txt"), output);
        }
        finally
        {
            await Task.WhenAll(sessions.Select(async session => await session.DisposeAsync()));
            foreach (var link in links)
            {
                link.Dispose();
            }

            foreach (var monitor in window.Monitors)
            {
                monitor.Dispose();
            }

            window.Close();
            trace.Listeners.Remove(listener);
            trace.Switch.Level = previous;
        }
        Assert.All(window.Monitors, monitor => Assert.Equal(nint.Zero, monitor.Viewport.Handle));
    });

    [Fact]
    public Task StandaloneControlRendersAn8KFrameUsingSoftwareFallback() => StaTest.Run(async () =>
    {
        var control = new VisionAnomaly.Controls.CameraMonitorControl { Title = "8K WARP 검증" };
        control.Viewport.DevicePreference = RenderDevicePreference.Software;
        var window = new Window { Content = control, Width = 800, Height = 500, ShowActivated = false, ShowInTaskbar = false, Left = -32000, Top = -32000 };
        var output = Path.Combine(StaTest.Root, "artifacts", "anomaly-monitor", $"8k-warp-{Guid.NewGuid():N}.png");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        try
        {
            window.Show();
            var pixels = new VisionImaging.Abstractions.PixelBuffer(7680, 4320, VisionImaging.Abstractions.PixelKind.Bgra32);
            for (var index = 0; index < pixels.Length; index += 4)
            {
                pixels.Bytes[index] = 80;
                pixels.Bytes[index + 1] = 140;
                pixels.Bytes[index + 2] = 50;
                pixels.Bytes[index + 3] = 255;
            }
            control.Present(new(1, 0, pixels), [new(new(3000, 1500, 1200, 900), "검증")]);
            await StaTest.Until(() => control.DisplayedSequence == 1, "8K presentation");
            await CaptureViewport(control.Viewport, output);
            Assert.Equal(7680, control.Viewport.Scene!.Image!.Width);
            using var stream = File.OpenRead(output);
            var image = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            Assert.True(image.PixelWidth > 0);
            var bitmap = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
            var rendered = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
            bitmap.CopyPixels(rendered, bitmap.PixelWidth * 4, 0);
            Assert.True(Enumerable.Range(0, rendered.Length / 4).Count(index => rendered[(index * 4) + 1] is > 120 and < 160) > 10000);
        }
        finally { control.Dispose(); window.Close(); }
    });

    private static async Task CaptureViewport(VisionCanvas.Controls.Dx12Viewport viewport, string path)
    {
        await Task.Delay(350);
        viewport.Capture(path);
        await StaTest.Until(() =>
        {
            try
            {
                using var stream = File.OpenRead(path);
                var bitmap = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                return bitmap.PixelWidth > 0;
            }
            catch (IOException) { return false; } // 비동기 PNG 저장 중에는 완성된 캡처가 아니다.
        }, "DX12 capture");
    }

    private static void SaveWpf(Window window, string path)
    {
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    private sealed class TestFiles : IMonitorFiles
    {
        public string Video { get; set; } = "";
        public string Model { get; set; } = "";
        public string? OpenVideo() => Video;
        public string? OpenModel() => Model;
        public string? SaveModel() => Model;
        public void ShowEvidence(string path) => Assert.True(File.Exists(path));
    }
}
