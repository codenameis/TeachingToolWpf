using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Interaction;
using VisionCanvas.Presentation;
using VisionCanvas.SampleShell.Services;
using VisionCanvas.WgpuSample.ViewModels;
using VisionCanvas.WgpuSample.Views;
using VisionImaging;
using VisionImaging.Abstractions;

namespace VisionCanvas.Wgpu.Tests;

public sealed class SampleTests
{
    [Fact]
    public Task RealWindowButtonsLoadDrawGroupPasteAndRestoreHistory() => Sta.RunAsync(async () =>
    {
        var window = new EditorWindow { ShowActivated = false, ShowInTaskbar = false };
        var editor = new EditorSession();
        var layout = new ViewportLayout();
        using var model = new EditorViewModel(editor, new(editor), layout,
            new(editor, window.Viewport, layout, new SourceImageLoader(new ImageLoader(ArtifactPath("sample-cache")))), new Picker(null));
        window.DataContext = model;
        try
        {
            window.Show();
            await model.SampleCommand.ExecuteAsync();
            Assert.Equal(8192, model.Scene.Image!.Width);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.DataBind);
            Assert.Same(model.Scene, window.Viewport.Scene);
            var buttons = Descendants(window).OfType<Button>().ToArray();
            Assert.All(buttons, button => Assert.NotNull(button.Command));
            Assert.Contains(buttons, button => Equals(button.Content, "1억 ROI") && ReferenceEquals(button.Command, model.HundredMillionCommand));
            Assert.False(model.UndoCommand.CanExecute(null));
            await model.DrawCommand.ExecuteAsync();
            var first = editor.State.Viewport.ImageToScreen(new(2000, 2000));
            var second = editor.State.Viewport.ImageToScreen(new(3000, 3000));
            model.Input.Down(first);
            model.Input.Move(second);
            model.Input.Up(second);
            await model.DrawCommand.ExecuteAsync();
            first = editor.State.Viewport.ImageToScreen(new(4000, 2000));
            second = editor.State.Viewport.ImageToScreen(new(5000, 3000));
            model.Input.Down(first);
            model.Input.Move(second);
            model.Input.Up(second);
            await model.SelectAllCommand.ExecuteAsync();
            await model.GroupCommand.ExecuteAsync();
            Assert.Equal(3, editor.State.Rois.Count);
            var grouped = editor.State;
            await model.CopyCommand.ExecuteAsync();
            await model.PasteCommand.ExecuteAsync();
            Assert.Equal(6, editor.State.Rois.Count);
            await model.UndoCommand.ExecuteAsync();
            Assert.Equal(grouped.Rois, editor.State.Rois);
            await model.DeleteCommand.ExecuteAsync();
            Assert.Empty(editor.State.Rois);
            await model.UndoCommand.ExecuteAsync();
            Assert.Equal(grouped.Rois, editor.State.Rois);
            await model.RedoCommand.ExecuteAsync();
            await model.AddCommand.ExecuteAsync();
            Assert.False(model.RedoCommand.CanExecute(null));
            await model.CancelCommand.ExecuteAsync();
            var capture = ArtifactPath($"sample-{Guid.NewGuid():N}.png");
            window.Viewport.Capture(capture);
            await WaitForCapture(capture);
            Assert.StartsWith("wgpu ·", model.RendererStatus);
        }
        finally
        {
            model.Dispose();
            window.Close();
        }
    });

    [Ken32Fact]
    public Task LocalKen32FileLoadsFitsAndShowsOriginalTile() => Sta.RunAsync(async () =>
    {
        var file = Path.GetFullPath(Environment.GetEnvironmentVariable("VISIONCANVAS_WGPU_KEN32")!);
        var root = ArtifactPath("ken32");
        Directory.CreateDirectory(root);
        using var host = new ViewportHost(new VisionCanvas.Wgpu.Controls.WgpuViewport());
        await host.Ready;
        var editor = new EditorSession();
        var layout = new ViewportLayout(host.Viewport.ActualWidth, host.Viewport.ActualHeight, host.Viewport.Dpi);
        using var images = new ImageCoordinator(editor, host.Viewport, layout, new ImageLoader(Path.Combine(root, "cache")));
        var started = Stopwatch.GetTimestamp();
        await images.ReplaceAsync(new(file, "ken32.png", 0, 0, ImageFileVersion.Read(file)));
        var loadMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        Assert.Equal(17280, images.Image!.Width);
        Assert.Equal(30720, images.Image.Height);
        var scene = new VisionCanvas.Rendering.RenderScene(editor.State, images.Image, images.Generation, layout.Width, layout.Height, layout.Dpi, new());
        var fit = await host.CaptureAsync(scene);
        var fitMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        Assert.True(fit.Pixels.Distinct().Count() > 80);
        var original = await host.CaptureAsync(scene with
        {
            Document = editor.State.WithViewport(new(1 / layout.Dpi, new(-7200 / layout.Dpi, -15000 / layout.Dpi))),
            PointSampling = true,
            MinimapOptions = new(false)
        });
        Assert.True(original.Pixels.Distinct().Count() > 80);
        Assert.False(fit.Pixels.SequenceEqual(original.Pixels));
        using (var source = await new ImageLoader(Path.Combine(root, "cache")).LoadAsync(editor.State.Image!, new Progress<string>(), CancellationToken.None))
        {
            Assert.Equal(PixelKind.Rgba32, source.Info.Pixels);
            AssertOriginalPixels(original, source, 7200, 15000);
        }
        var capture = Path.Combine(root, $"original-tile-{Guid.NewGuid():N}.png");
        host.Viewport.Capture(capture);
        await WaitForCapture(capture);
        Assert.Empty(host.Errors);
        File.WriteAllText(Path.Combine(root, $"wgpu-result-{Guid.NewGuid():N}.json"), JsonSerializer.Serialize(new
        {
            images.Image,
            LoadMilliseconds = loadMilliseconds,
            LoadThroughCaptureMilliseconds = fitMilliseconds,
            Note = "Wgpu GPU, PNG/피라미드 준비 포함. Capture는 readback/PNG/20ms polling을 포함하며 물리 scan-out 또는 60FPS 측정이 아님. OS 캐시는 초기화하지 않음."
        }, new JsonSerializerOptions { WriteIndented = true }));
    });

    private static void AssertOriginalPixels(Capture capture, ITileSource source, int offsetX, int offsetY)
    {
        // 서로 다른 타일과 타일 경계를 포함한다. 축소 overview가 남아 있는 캡처는 통과하지 못한다.
        int[] columns = [30, 233, 479, 480, 481, 777];
        int[] rows = [10, 240, 359, 360, 361];
        foreach (var y in rows.Where(value => value < capture.Height))
        {
            foreach (var x in columns.Where(value => value < capture.Width))
            {
                var sourceX = offsetX + x;
                var sourceY = offsetY + y;
                using var tile = source.Read(new(0, sourceX / ImageInfo.TileSize, sourceY / ImageInfo.TileSize), CancellationToken.None);
                var expected = (sourceY % ImageInfo.TileSize * tile.Stride) + (sourceX % ImageInfo.TileSize * 4);
                var actual = ((y * capture.Width) + x) * 4;
                for (var channel = 0; channel < 3; channel++)
                {
                    Assert.InRange(Math.Abs(tile.Bytes[expected + (2 - channel)] - capture.Pixels[actual + channel]), 0, 1);
                }
            }
        }
    }

    private static IEnumerable<System.Windows.DependencyObject> Descendants(System.Windows.DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }

    internal static string ArtifactPath(string file)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "TeachingToolWpf.slnx")))
        {
            root = root.Parent;
        }
        var path = Path.Combine(root?.FullName ?? throw new InvalidOperationException("Repository not found"), "artifacts", "wgpu-port-validation", file);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    private static async Task WaitForCapture(string path)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(30))
        {
            await Task.Delay(20);
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
                if (stream.Length > 0)
                {
                    return;
                }
            }
            catch (IOException)
            {
                // 렌더러가 PNG를 닫을 때까지 기다린다.
            }
        }
        throw new TimeoutException("Capture incomplete");
    }

    private sealed class Picker(ImageReference? image) : IImageFilePicker
    {
        public ImageReference? Pick() => image;
    }
}

public sealed class Ken32FactAttribute : FactAttribute
{
    public Ken32FactAttribute()
    {
        if (Environment.GetEnvironmentVariable("VISIONCANVAS_WGPU_KEN32") is null)
        {
            Skip = "로컬 ken32.png 검증은 VISIONCANVAS_WGPU_KEN32 경로를 지정해 실행합니다.";
        }
    }
}

