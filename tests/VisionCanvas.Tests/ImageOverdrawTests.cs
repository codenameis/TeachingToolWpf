using VisionCanvas.Core.Editing;
using VisionCanvas.Imaging;
using VisionCanvas.Rendering;
using VisionImaging.Abstractions;

namespace VisionCanvas.Tests;

public sealed class ImageOverdrawTests
{
    [Theory]
    [InlineData(PixelKind.Gray8)]
    [InlineData(PixelKind.Gray16)]
    public void GrayscaleIsOpaqueRegardlessOfIntensity(PixelKind kind)
    {
        using var pixels = new PixelBuffer(3, 2, kind);
        pixels.Span.Clear();
        Assert.True(SpritePipeline.PixelsAreOpaque(pixels));
    }

    [Theory]
    [InlineData(PixelKind.Rgba32)]
    [InlineData(PixelKind.Bgra32)]
    public void EveryAlphaMustBeOpaqueIncludingLastPixel(PixelKind kind)
    {
        using var pixels = new PixelBuffer(3, 2, kind);
        pixels.Span.Clear();
        for (var i = 3; i < pixels.Length; i += 4)
        {
            pixels.Span[i] = 255;
        }
        Assert.True(SpritePipeline.PixelsAreOpaque(pixels));
        pixels.Span[^1] = 254;
        Assert.False(SpritePipeline.PixelsAreOpaque(pixels));
        pixels.Span[^1] = 255;
        pixels.Span[3] = 0;
        Assert.False(SpritePipeline.PixelsAreOpaque(pixels));
    }

    [Theory]
    [InlineData(1, 0, 0, 1)]
    [InlineData(.3, 17.25, -20.75, 1.5)]
    [InlineData(2.1, -600.25, -501.5, 1.25)]
    public void CompleteOpaqueTilesDrawOnceAfterPanZoomAndDpiChange(double scale, double x, double y, double dpi)
    {
        var scene = Scene(1025, 769) with
        {
            Document = DocumentState.Empty with
            {
                Viewport = new(scale, new(x, y))
            },
            Dpi = dpi
        };
        var tiles = Tiles(scene);
        var mesh = Draw(scene, tiles);
        var visible = TilePlanner.Visible(scene.Image!, scene.Document.Viewport, scene.Width, scene.Height, scene.Dpi);
        Assert.Equal(visible.Select(key => tiles[key]), mesh.Batches.Select(batch => batch.Texture));
        Assert.Equal(visible.Count * 6, mesh.Vertices.Count);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("transparent")]
    [InlineData("uninitialized")]
    [InlineData("short")]
    public void IncompleteOrTransparentCoverageKeepsOverview(string condition)
    {
        var scene = Scene(1025, 769);
        var tiles = Tiles(scene);
        var key = new TileKey(0, 0, 0);
        switch (condition)
        {
            case "missing":
                tiles.Remove(key);
                break;
            case "transparent":
                tiles[key].IsOpaque = false;
                break;
            case "uninitialized":
                tiles[key].HasPixels = false;
                break;
            case "short":
                tiles[key] = Texture(511, 512);
                break;
        }
        var mesh = Draw(scene, tiles);
        Assert.Same(tiles[new(scene.Image!.Levels - 1, 0, 0)], mesh.Batches[0].Texture);
    }

    [Fact]
    public void OneLevelOpaqueImageIsNotDrawnTwice()
    {
        var scene = Scene(300, 200);
        var mesh = Draw(scene, Tiles(scene));
        Assert.Single(mesh.Batches);
        Assert.Equal(6, mesh.Vertices.Count);
    }

    [Fact]
    public void MinimapStillUsesOverviewAfterMainImageIsCovered()
    {
        var scene = Scene(1025, 769);
        scene = scene with
        {
            Document = scene.Document with
            {
                Image = new("test", "test", 1025, 769)
            }
        };
        var tiles = Tiles(scene);
        var painter = new ScenePainter();
        var mesh = Draw(scene, tiles, painter);
        var overview = tiles[new(scene.Image!.Levels - 1, 0, 0)];
        Assert.DoesNotContain(mesh.Batches.Take(painter.RoiBatchBoundary), batch => batch.Texture == overview);
        Assert.Contains(mesh.Batches.Skip(painter.RoiBatchBoundary), batch => batch.Texture == overview);
    }

    private static RenderScene Scene(int width, int height)
        => new(DocumentState.Empty with
        {
            Viewport = new(1, new(0, 0))
        },
            new("test", "test", width, height, PixelKind.Rgba32, ImageInfo.CalculateLevelCount(width, height)),
            1, 800, 600, 1, new());

    private static Dictionary<TileKey, GpuTexture> Tiles(RenderScene scene)
    {
        var image = scene.Image!;
        var result = new Dictionary<TileKey, GpuTexture>();
        foreach (var key in TilePlanner.Visible(image, scene.Document.Viewport, scene.Width, scene.Height, scene.Dpi)
            .Append(new(image.Levels - 1, 0, 0)).Distinct())
        {
            var factor = 1 << key.Level;
            result.Add(key, Texture(Math.Min(512, (int)Math.Ceiling(image.Width / (double)factor) - (key.X * 512)),
                Math.Min(512, (int)Math.Ceiling(image.Height / (double)factor) - (key.Y * 512))));
        }
        return result;
    }

    private static GpuTexture Texture(int width, int height)
        => new(null!, 0, PixelKind.Rgba32, width, height, _ => { })
        {
            HasPixels = true,
            IsOpaque = true
        };

    private static SpriteMesh Draw(RenderScene scene, IReadOnlyDictionary<TileKey, GpuTexture> tiles, ScenePainter? painter = null)
    {
        var white = Texture(1, 1);
        using var filters = new GpuFilterPreview();
        return (painter ?? new ScenePainter()).Build(scene, white, tiles, _ => white, _ => white, null, default, filters, null);
    }
}
