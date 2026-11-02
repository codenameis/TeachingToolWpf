using System.Diagnostics;
using SkiaSharp;
using VisionCanvas.Controls;
using VisionCanvas.Core.Editing;
using VisionCanvas.Imaging.Filters;
using VisionCanvas.Layers;
using VisionCanvas.Rendering;
using VisionCanvas.Skia.Controls;
using VisionCanvas.Skia.Rendering;
using VisionGeometry;
using VisionImaging.Abstractions;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace VisionCanvas.Skia.Tests;

public sealed class RenderingTests
{
    [Fact]
    public void SkiaViewportIsIndependentFromDx12Viewport()
    {
        Assert.Equal(typeof(ViewportControlBase), typeof(SkiaViewport).BaseType);
        Assert.DoesNotContain(typeof(SkiaViewport).Assembly.GetReferencedAssemblies(),
            reference => reference.Name is not null && reference.Name.Contains("Direct3D12", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(PixelKind.Gray8)]
    [InlineData(PixelKind.Gray16)]
    [InlineData(PixelKind.Bgra32)]
    [InlineData(PixelKind.Rgba32)]
    public void TexturePreservesChannelsAndOwnership(PixelKind kind)
    {
        using var pixels = new PixelBuffer(2, 1, kind);
        if (kind == PixelKind.Gray16)
        {
            pixels.Span[0] = 0;
            pixels.Span[1] = 0;
            pixels.Span[2] = 255;
            pixels.Span[3] = 255;
        }
        else if (kind == PixelKind.Gray8)
        {
            pixels.Span[0] = 0;
            pixels.Span[1] = 255;
        }
        else
        {
            new byte[] { 10, 20, 30, 255, 40, 50, 60, 128 }.CopyTo(pixels.Bytes, 0);
        }
        var original = pixels.Span.ToArray();
        using var texture = new SkiaTexture(2, 1, kind);
        texture.Update(pixels);
        Assert.Equal(original, pixels.Span.ToArray());
        if (kind is PixelKind.Gray8 or PixelKind.Gray16)
        {
            Assert.Equal(SKColors.Black, texture.Bitmap.GetPixel(0, 0));
            Assert.Equal(SKColors.White, texture.Bitmap.GetPixel(1, 0));
            Assert.True(texture.IsOpaque);
            Assert.Equal(2, texture.Bytes);
        }
        else
        {
            var first = texture.Bitmap.GetPixel(0, 0);
            Assert.Equal(kind == PixelKind.Bgra32 ? 30 : 10, first.Red);
            Assert.Equal(20, first.Green);
            Assert.Equal(kind == PixelKind.Bgra32 ? 10 : 30, first.Blue);
            Assert.Equal(128, texture.Bitmap.GetPixel(1, 0).Alpha);
            Assert.False(texture.IsOpaque);
        }
    }

    [Fact]
    public Task ShapesMasksLayersAndUndoRenderThroughSkia() => Sta.RunAsync(async () =>
    {
        using var host = new ViewportHost(new SkiaViewport());
        await host.Ready;
        var editor = new EditorSession();
        editor.InitializeImage(new("test", "test", 600, 400), new(1, default));
        var parent = editor.AddParent(new(new(130, 150), 200, 220, 0));
        var circle = editor.AddChild(parent, RoiGeometry.Create(RoiShapeKind.Circle, new(130, 150), 90, 90));
        var triangle = editor.Add(RoiGeometry.Create(RoiShapeKind.Triangle, new(330, 130), 120, 100).RotateAround(new(330, 130), 23));
        editor.Add(RoiGeometry.Create(RoiShapeKind.Polygon, new(470, 250), 150, 110));
        editor.AddMask(circle, new(MaskShape.Ellipse, new(.2, .2, .8, .8)));
        editor.Select([circle]);
        var filtered = new FilteredRoi(triangle, editor.State.Rois[triangle].Geometry, 16, 16, Enumerable.Repeat((byte)70, 256).ToArray());
        var scene = new RenderScene(editor.State, null, 0, 600, 400, 1, new(), FilterPreview: new([filtered], 0)) { MinimapOptions = new(false) };
        var guide = host.Viewport.Layers.Create("guide", LayerSpace.Screen);
        guide.Add(new LineGraphic(new(20, 350), new(550, 350), new(0, 1, 0, 1), 3));
        var first = await host.CaptureAsync(scene);
        var line = At(first, 280, 350, host.Viewport.Dpi);
        Assert.True(line.Green > 240 && line.Red < 10 && line.Blue < 10, $"Guide color: {line}");
        var mask = At(first, 130, 150, host.Viewport.Dpi);
        Assert.True(mask.Red > mask.Green + 10, $"Mask center: {mask}");
        var outside = At(first, 180, 150, host.Viewport.Dpi);
        Assert.Equal(new SKColor(24, 28, 36, 255), outside);
        Assert.True(first.Pixels.Count(value => value > 200) > 1000);
        editor.Select([triangle]);
        var before = await host.CaptureAsync(scene with
        {
            Document = editor.State
        });
        editor.RotateSelection(17, new(330, 130));
        var after = await host.CaptureAsync(scene with
        {
            Document = editor.State
        });
        Assert.False(before.Pixels.SequenceEqual(after.Pixels));
        editor.Undo();
        Assert.Equal(before.Pixels, (await host.CaptureAsync(scene with
        {
            Document = editor.State
        })).Pixels);
        host.Viewport.Capture(SampleTests.ArtifactPath("shapes.png"));
        Assert.Empty(host.Errors);
    });

    [Theory]
    [InlineData(PixelKind.Gray8)]
    [InlineData(PixelKind.Gray16)]
    public Task EightKLiveRestartAndResizePreservePixels(PixelKind kind) => Sta.RunAsync(async () =>
    {
        using var host = new ViewportHost(new SkiaViewport());
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
        Assert.InRange(At(first, 300, 200, host.Viewport.Dpi).Red, (byte)119, (byte)121);
        await host.Viewport.RestartAsync();
        Assert.Equal(first.Pixels, (await host.CaptureAsync(scene)).Pixels);
        host.Window.WindowState = System.Windows.WindowState.Minimized;
        await Task.Delay(40);
        host.Window.WindowState = System.Windows.WindowState.Normal;
        host.Window.Width = 720;
        host.Window.UpdateLayout();
        var resized = await host.CaptureAsync(scene);
        Assert.Equal((int)Math.Ceiling(host.Viewport.ActualWidth * host.Viewport.Dpi), resized.Width);
        Assert.Empty(host.Errors);
    });

    [Fact]
    public Task BatchOrdinaryRoisAndPartialSelectionRemainVisible() => Sta.RunAsync(async () =>
    {
        using var host = new ViewportHost(new SkiaViewport());
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

    private static SKColor At(Capture capture, double x, double y, double dpi)
    {
        var offset = (((int)(y * dpi) * capture.Width) + (int)(x * dpi)) * 4;
        return new(capture.Pixels[offset + 2], capture.Pixels[offset + 1], capture.Pixels[offset], capture.Pixels[offset + 3]);
    }
}
