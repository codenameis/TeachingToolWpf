using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Media.Imaging;
using VisionCanvas.Controls;
using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Geometry;
using VisionCanvas.Core.Interaction;
using VisionCanvas.Imaging.Filters;
using VisionCanvas.Layers;
using VisionCanvas.Rendering;
using VisionCanvas.Vulkan.Controls;
using VisionGeometry;
using VisionImaging;
using VisionImaging.Abstractions;

namespace VisionCanvas.Vulkan.Tests;

public sealed class ViewportTests
{
    [Fact]
    public void VulkanViewportIsIndependentFromDx12Viewport()
    {
        Assert.Equal(typeof(ViewportControlBase), typeof(VulkanViewport).BaseType);
        Assert.DoesNotContain(typeof(VulkanViewport).Assembly.GetReferencedAssemblies(),
            reference => reference.Name is not null && reference.Name.Contains("Direct3D12", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public Task ShapesLabelsMasksFiltersAndLayersRenderAndRestoreWithUndo() => Sta.RunAsync(async () =>
    {
        var editor = new EditorSession();
        editor.InitializeImage(new("test", "test", 600, 400), new(1, new(0, 0)));
        var parent = editor.AddParent(new(new(130, 150), 200, 220, 0));
        var circle = editor.AddChild(parent, RoiGeometry.Create(RoiShapeKind.Circle, new(130, 150), 90, 90));
        var triangle = editor.Add(RoiGeometry.Create(RoiShapeKind.Triangle, new(330, 130), 120, 100).RotateAround(new(330, 130), 23));
        editor.Add(RoiGeometry.Create(RoiShapeKind.Polygon, new(470, 250), 150, 110));
        editor.AddMask(circle, new(MaskShape.Ellipse, new(.2, .2, .8, .8)));
        editor.Select([circle]);
        var filtered = new FilteredRoi(triangle, editor.State.Rois[triangle].Geometry, 16, 16, Enumerable.Repeat((byte)70, 256).ToArray());
        var marker = new MatchMarker([new(20, 20), new(170, 20), new(170, 60), new(20, 60)], new(95, 40), 0, "검사 표시", new(0, 1, 0, 1), [])
        {
            InnerContours = [[new(40, 30), new(100, 30), new(100, 50), new(40, 50)]]
        };
        var scene = new RenderScene(editor.State, null, 0, 600, 400, 1, new(), FilterPreview: new([filtered], 0),
            Inspection: new("결과", Vector4.One, [], [marker])
            {
                BatchLabels = true
            })
        {
            MinimapOptions = new(false)
        };
        using var vulkan = new ViewportHost(new VulkanViewport());
        await vulkan.Ready;
        vulkan.Viewport.Layers.Create("guide", LayerSpace.Screen).Add(new LineGraphic(new(20, 350), new(550, 350), new(0, 1, 0, 1), 3));
        var first = await vulkan.CaptureAsync(scene);
        Assert.True(first.Pixels.Count(value => value > 200) > 1000, "Contours, Korean labels and handles must be visible.");
        editor.Select([triangle]);
        var before = editor.State;
        editor.RotateSelection(17, new(330, 130));
        var after = await vulkan.CaptureAsync(scene with
        {
            Document = editor.State
        });
        Assert.False(first.Pixels.SequenceEqual(after.Pixels));
        editor.Undo();
        Assert.Equal(before, editor.State);
        var restored = await vulkan.CaptureAsync(scene with
        {
            Document = editor.State
        });
        Assert.False(after.Pixels.SequenceEqual(restored.Pixels));
        Assert.Empty(vulkan.Errors);
    });

    [Fact]
    public Task BatchOrdinaryRoisAndPartialSelectionRemainVisible() => Sta.RunAsync(async () =>
    {
        using var host = new ViewportHost(new VulkanViewport());
        await host.Ready;
        var editor = new EditorSession();
        editor.InitializeImage(new("grid", "grid", 600, 400), ViewportTransform.Identity);
        var geometries = new List<RoiGeometry>(400);
        for (var row = 0; row < 20; row++)
        {
            for (var column = 0; column < 20; column++)
            {
                geometries.Add(new(new(100 + (column * 12), 80 + (row * 12)), 8, 8, 0));
            }
        }
        var ids = editor.AddRange(geometries, selectAdded: true, isNameVisible: false);
        var scene = new RenderScene(editor.State, null, 0, 600, 400, 1, new()) { MinimapOptions = new(false) };
        var full = await host.CaptureAsync(scene);
        editor.Select(ids.Take(10));
        var partial = await host.CaptureAsync(scene with
        {
            Document = editor.State
        });
        Assert.False(full.Pixels.SequenceEqual(partial.Pixels));
        Assert.Empty(host.Errors);
    });

    [Fact]
    public Task EightKSourceZoomResizeMinimizeAndRestartKeepImageAndHistory() => Sta.RunAsync(async () =>
    {
        using var host = new ViewportHost(new VulkanViewport());
        await host.Ready;
        var source = new ProceduralTileSource(8192, 8192);
        var info = source.Info;
        var generation = host.Viewport.SetSource(source);
        var editor = new EditorSession();
        editor.InitializeImage(new(info.Key, info.Name, info.Width, info.Height), ViewportTransform.Fit(info.Width, info.Height, 600, 400));
        editor.Add(new(new(4000, 4000), 1200, 1200, 25));
        var scene = new RenderScene(editor.State, info, generation, 600, 400, 1, new());
        var first = await host.CaptureAsync(scene);
        Assert.True(first.Pixels.Distinct().Count() > 50);
        var state = editor.State;
        host.Window.WindowState = WindowState.Minimized;
        await Task.Delay(40);
        host.Window.WindowState = WindowState.Normal;
        host.Window.Width = 720;
        host.Window.Height = 500;
        host.Window.UpdateLayout();
        await host.Viewport.RestartAsync();
        var changed = await host.CaptureAsync(scene with
        {
            Document = editor.State.WithViewport(new(1, new(-3700, -3800)))
        });
        Assert.Equal((int)Math.Ceiling(host.Viewport.ActualWidth * host.Viewport.Dpi), changed.Width);
        Assert.NotEqual(first.Width, changed.Width);
        Assert.Same(state, editor.State);
        Assert.True(editor.CanUndo);
        Assert.Empty(host.Errors);
    });

    [Theory]
    [InlineData(PixelKind.Gray8)]
    [InlineData(PixelKind.Gray16)]
    public Task EightKLiveFrameAndRestartRetainLastPixels(PixelKind kind) => Sta.RunAsync(async () =>
    {
        using var host = new ViewportHost(new VulkanViewport());
        await host.Ready;
        var pixels = new PixelBuffer(7680, 4320, kind);
        pixels.Span.Fill(120);
        host.Viewport.PublishFrame(new(1, Stopwatch.GetTimestamp(), pixels));
        var document = DocumentState.Empty.WithViewport(ViewportTransform.Fit(7680, 4320, 600, 400));
        var scene = new RenderScene(document, new("live", "8K", 7680, 4320, kind, 1), 1, 600, 400, 1, new(), IsLive: true)
        {
            MinimapOptions = new(false)
        };
        var first = await host.CaptureAsync(scene);
        var center = ((first.Height / 2 * first.Width) + (first.Width / 2)) * 4;
        Assert.InRange(first.Pixels[center], 119, 121);
        await host.Viewport.RestartAsync();
        AssertEquivalent(first, await host.CaptureAsync(scene));
        Assert.Empty(host.Errors);
    });

    internal static void AssertEquivalent(Capture expected, Capture actual)
    {
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        var different = 0;
        long totalError = 0;
        for (var index = 0; index < actual.Pixels.Length; index += 4)
        {
            var error = 0;
            for (var channel = 0; channel < 3; channel++)
            {
                error += Math.Abs(expected.Pixels[index + channel] - actual.Pixels[index + channel]);
            }
            if (error > 12)
            {
                different++;
            }
            totalError += error;
        }
        var pixels = actual.Width * actual.Height;
        Assert.True(different < pixels * .015 && totalError / (pixels * 3d) < 1.5,
            $"Vulkan capture difference: {different}/{pixels} pixels, mean channel error {totalError / (pixels * 3d):F4}.");
    }
}

internal sealed record Capture(int Width, int Height, byte[] Pixels);

internal sealed class ViewportHost : IDisposable
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ViewportControlBase Viewport
    {
        get;
    }
    public Window Window
    {
        get;
    }
    public List<string> Errors { get; } = [];
    public Task Ready => _ready.Task.WaitAsync(TimeSpan.FromSeconds(20));

    public ViewportHost(ViewportControlBase viewport)
    {
        Viewport = viewport;
        Window = new()
        {
            Content = viewport,
            Width = 600,
            Height = 400,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize
        };
        viewport.RendererStatusChanged += status =>
        {
            if (status.StartsWith("렌더 오류:", StringComparison.Ordinal))
            {
                Errors.Add(status);
                _ready.TrySetException(new InvalidOperationException(status));
            }
            else
            {
                _ready.TrySetResult();
            }
        };
        viewport.Tiles.Failed += message => viewport.Dispatcher.BeginInvoke(() => Errors.Add(message));
        Window.Show();
        Window.UpdateLayout();
    }

    public async Task<Capture> CaptureAsync(RenderScene scene)
    {
        var path = Path.Combine(Path.GetTempPath(), $"vulkan-scene-{Guid.NewGuid():N}.png");
        try
        {
            Viewport.Submit(scene with
            {
                Width = Viewport.ActualWidth,
                Height = Viewport.ActualHeight,
                Dpi = Viewport.Dpi
            });
            Viewport.Capture(path);
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(25))
            {
                Assert.Empty(Errors);
                await Task.Delay(20);
                try
                {
                    using var stream = File.OpenRead(path);
                    var bitmap = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                    var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
                    bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
                    return new(bitmap.PixelWidth, bitmap.PixelHeight, pixels);
                }
                catch (IOException)
                {
                    // 파일 준비/PNG 저장 완료를 기다린다.
                }
            }
            throw new TimeoutException("Vulkan Canvas 캡처 시간 초과");
        }
        finally
        {
            File.Delete(path);
        }
    }

    public void Dispose()
    {
        Viewport.Dispose();
        Window.Close();
    }
}
