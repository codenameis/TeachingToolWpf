using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VisionCanvas.Controls;
using VisionCanvas.Core.Editing;
using VisionCanvas.MvvmSample.Services;
using VisionCanvas.MvvmSample.ViewModels;
using VisionCanvas.MvvmSample.Views;
using VisionCanvas.Presentation;
using VisionCanvas.Rendering;
using VisionGeometry;
using VisionImaging;
using VisionImaging.Abstractions;

namespace VisionCanvas.MvvmSample.Tests;

public sealed partial class EditorWindowTests
{
    [Fact]
    public Task LoadingKeepsNativeEscapeCancellationWhileInspectionLockBlocksIt() => OnStaAsync(async () =>
    {
        var window = new EditorWindow { ShowActivated = false, ShowInTaskbar = false };
        var editor = new EditorSession();
        var layout = new ViewportLayout();
        var loader = new NavigationLoader();
        using var model = new EditorViewModel(editor, new(editor), layout,
            new(editor, window.Viewport, layout, loader), new NavigationPicker());
        window.DataContext = model;
        try
        {
            window.Show();
            var loading = model.SampleCommand.ExecuteAsync();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            Assert.False(model.IsInputEnabled);
            Assert.True(window.Viewport.IsInputEnabled);
            Assert.True(model.Input.NavigationOnly);
            SendMessage(window.Viewport.Handle, 0x0100, 0x1B, 0);
            Assert.True(loader.Token.IsCancellationRequested);
            loader.Completion.SetResult(new ProceduralTileSource(128, 128));
            await loading;
            Assert.Null(editor.State.Image);
            model.Navigation.IsLocked = true;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            Assert.False(window.Viewport.IsInputEnabled);
        }
        finally
        {
            model.Dispose();
            window.Close();
        }
    });

    [Theory]
    [InlineData(RenderDevicePreference.Automatic)]
    [InlineData(RenderDevicePreference.Software)]
    public Task NavigationListAndLockKeepNativeInputAndLiveFramesSeparate(RenderDevicePreference device) => OnStaAsync(async () =>
    {
        var output = Path.Combine(RepositoryRoot(), "artifacts", "canvas-navigation", device.ToString());
        Directory.CreateDirectory(output);
        var window = new EditorWindow { ShowActivated = false, ShowInTaskbar = false };
        window.Viewport.DevicePreference = device;
        var editor = new EditorSession();
        var layout = new ViewportLayout();
        using var model = new EditorViewModel(editor, new(editor), layout,
            new(editor, window.Viewport, layout, new SourceImageLoader(new ImageLoader(Path.Combine(output, "cache")))), new NavigationPicker());
        window.DataContext = model;
        try
        {
            window.Show();
            await model.SampleCommand.ExecuteAsync();
            var target = editor.Add(new(new(5000, 4000), 180, 100, 32));
            var selected = editor.Add(new(new(2000, 2000), 100, 100, 0));
            await Drain();
            SendMessage(window.Viewport.Handle, 0x0100, 0x27, 0);
            Assert.Equal(new PointD(2001, 2000), editor.State.Rois[selected].Geometry.Center);
            var bar = Descendants(window).OfType<ViewportNavigationBar>().Single();
            var combo = Descendants(bar).OfType<ComboBox>().Single();
            combo.IsDropDownOpen = true;
            await Drain();
            combo.SelectedItem = model.Navigation.Targets.Single(item => item.Id == target);
            combo.IsDropDownOpen = false;
            await Drain();
            Assert.Equal(selected, editor.State.Selection.Single());
            Assert.True((editor.State.Viewport.ImageToScreen(new(5000, 4000)) - new PointD(layout.Width / 2, layout.Height / 2)).Length < 1e-8);
            var beforeRois = editor.State.Rois;
            combo.Focus();
            combo.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(combo), 0, Key.Down)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent
            });
            Assert.Same(beforeRois, editor.State.Rois);
            model.Navigation.FitCommand.Execute(null);
            Assert.Equal(ViewportTransform.Fit(8192, 8192, layout.Width, layout.Height), editor.State.Viewport);
            var toggle = Descendants(bar).OfType<CheckBox>().Single();
            toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
            await Drain();
            Assert.False(window.Viewport.IsInputEnabled);
            var before = editor.State;
            SendMessage(window.Viewport.Handle, 0x0100, 0x2E, 0);
            SendMessage(window.Viewport.Handle, 0x0100, 0x27, 0);
            SendMessage(window.Viewport.Handle, 0x020A, 120 << 16, 0);
            Assert.Equal(before, editor.State);

            // WPF 도구 영역의 실제 배치 확인용. HWND 영상 픽셀은 아래 렌더러 캡처로 별도 검증한다.
            var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(Path.Combine(output, "navigation-ui.png")))
            {
                png.Save(file);
            }

            var scene = new RenderScene(DocumentState.Empty with
            {
                Viewport = ViewportTransform.Fit(7680, 4320, layout.Width, layout.Height)
            }, new("live", "8K", 7680, 4320, PixelKind.Gray8, 1), 1, layout.Width, layout.Height, layout.Dpi,
                new(), IsLive: true)
            {
                MinimapOptions = new(false)
            };
            window.Viewport.Submit(scene);
            var pixelWidth = (int)Math.Ceiling(window.Viewport.ActualWidth * window.Viewport.Dpi);
            var pixelHeight = (int)Math.Ceiling(window.Viewport.ActualHeight * window.Viewport.Dpi);
            var center = ((pixelHeight / 2 * pixelWidth) + (pixelWidth / 2)) * 4;
            foreach (var level in new byte[] { 50, 180 })
            {
                var pixels = new PixelBuffer(7680, 4320, PixelKind.Gray8);
                pixels.Span.Fill(level);
                window.Viewport.PublishFrame(new(level, Stopwatch.GetTimestamp(), pixels));
                await CaptureViewportAsync(window.Viewport, Path.Combine(output, $"locked-frame-{level}.png"),
                    bytes => bytes[center] >= level - 1 && bytes[center] <= level + 1 &&
                        bytes[center + 1] == bytes[center] && bytes[center + 2] == bytes[center]);
            }
            Assert.False(window.Viewport.IsInputEnabled);
            toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
            await Drain();
            Assert.True(window.Viewport.IsInputEnabled);
            SendMessage(window.Viewport.Handle, 0x0100, 0x27, 0);
            Assert.Equal(new PointD(2002, 2000), editor.State.Rois[selected].Geometry.Center);
        }
        finally
        {
            model.Dispose();
            window.Close();
        }
        Task Drain() => window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle).Task;
    });

    private sealed class NavigationPicker : IImageFilePicker
    {
        public ImageReference? Pick() => null;
    }

    private sealed class NavigationLoader : IImageLoader
    {
        public TaskCompletionSource<ITileSource> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token
        {
            get; private set;
        }
        public Task<ITileSource> LoadAsync(ImageReference image, IProgress<string> progress, CancellationToken cancellationToken)
        {
            Token = cancellationToken;
            return Completion.Task;
        }
    }
}
