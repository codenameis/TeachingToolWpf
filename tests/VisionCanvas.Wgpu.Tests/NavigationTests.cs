using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Interaction;
using VisionCanvas.Presentation;
using VisionCanvas.Rendering;
using VisionCanvas.Wgpu.Controls;
using VisionCanvas.WgpuSample.Services;
using VisionCanvas.WgpuSample.ViewModels;
using VisionCanvas.WgpuSample.Views;
using VisionGeometry;
using VisionImaging;
using VisionImaging.Abstractions;

namespace VisionCanvas.Wgpu.Tests;

public sealed class NavigationTests
{
    [Fact]
    public Task RealWindowLocksNativeInputAndListFocusDoesNotSelectRoi() => Sta.RunAsync(async () =>
    {
        var window = new EditorWindow { ShowActivated = false, ShowInTaskbar = false };
        var editor = new EditorSession();
        var layout = new ViewportLayout();
        using var model = new EditorViewModel(editor, new(editor), layout,
            new(editor, window.Viewport, layout, new SourceImageLoader(new ImageLoader(SampleTests.ArtifactPath("navigation-cache")))), new Picker());
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
            Assert.True(model.HandleKey(Key.Down, false, true));
            Assert.Equal(new PointD(2001, 2010), editor.State.Rois[selected].Geometry.Center);
            await model.UndoCommand.ExecuteAsync();
            Assert.Equal(new PointD(2001, 2000), editor.State.Rois[selected].Geometry.Center);
            model.Navigation.RefreshTargets();
            var before = editor.State;
            model.Navigation.Target = model.Navigation.Targets.Single(item => item.Id == target);
            Assert.Same(before.Selection, editor.State.Selection);
            Assert.Same(before.Rois, editor.State.Rois);
            Assert.True((editor.State.Viewport.ImageToScreen(new(5000, 4000)) - new PointD(layout.Width / 2, layout.Height / 2)).Length < 1e-8);
            model.Navigation.FitCommand.Execute(null);
            Assert.Equal(ViewportTransform.Fit(8192, 8192, layout.Width, layout.Height), editor.State.Viewport);
            var bar = FindChildren(window).OfType<VisionCanvas.Controls.ViewportNavigationBar>().Single();
            var toggle = FindChildren(bar).OfType<CheckBox>().Single();
            toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
            await Drain();
            Assert.True(model.Navigation.IsLocked);
            Assert.False(window.Viewport.IsInputEnabled);
            Assert.False(model.DeleteCommand.CanExecute(null));
            before = editor.State;
            foreach (var key in new[] { 0x25, 0x26, 0x27, 0x28, 0x2E, 0x1B, 0x41, 0x52 })
            {
                SendMessage(window.Viewport.Handle, 0x0100, key, 0);
            }
            SendMessage(window.Viewport.Handle, 0x020A, 120 << 16, 0);
            SendMessage(window.Viewport.Handle, 0x0201, 0, 100 | (100 << 16));
            SendMessage(window.Viewport.Handle, 0x0200, 0, 180 | (160 << 16));
            SendMessage(window.Viewport.Handle, 0x0202, 0, 180 | (160 << 16));
            Assert.Equal(before, editor.State);
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

    [Fact]
    public Task LockedViewportStillDisplaysNewEightKFrames() => Sta.RunAsync(async () =>
    {
        using var host = new ViewportHost(new WgpuViewport());
        await host.Ready;
        host.Viewport.IsInputEnabled = false;
        var document = DocumentState.Empty.WithViewport(ViewportTransform.Fit(7680, 4320, 600, 400));
        var scene = new RenderScene(document, new("live", "8K", 7680, 4320, PixelKind.Gray8, 1),
            1, 600, 400, 1, new(), IsLive: true)
        {
            MinimapOptions = new(false)
        };
        var first = new PixelBuffer(7680, 4320, PixelKind.Gray8);
        first.Span.Fill(50);
        host.Viewport.PublishFrame(new(1, Stopwatch.GetTimestamp(), first));
        var capture = await host.CaptureAsync(scene);
        var center = ((capture.Height / 2 * capture.Width) + (capture.Width / 2)) * 4;
        Assert.InRange(capture.Pixels[center], 49, 51);
        var next = new PixelBuffer(7680, 4320, PixelKind.Gray8);
        next.Span.Fill(180);
        host.Viewport.PublishFrame(new(2, Stopwatch.GetTimestamp(), next));
        capture = await host.CaptureAsync(scene);
        Assert.InRange(capture.Pixels[center], 179, 181);
        Assert.False(host.Viewport.IsInputEnabled);
        Assert.Empty(host.Errors);
    });

    private static IEnumerable<System.Windows.DependencyObject> FindChildren(System.Windows.DependencyObject parent)
    {
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var descendant in FindChildren(child))
            {
                yield return descendant;
            }
        }
    }

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint window, int message, nint parameter, nint position);

    private sealed class Picker : IImageFilePicker
    {
        public ImageReference? Pick() => null;
    }
}

