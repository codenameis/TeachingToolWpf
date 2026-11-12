using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Geometry;
using VisionCanvas.Core.Interaction;
using VisionCanvas.Imaging;
using VisionCanvas.Rendering;
using VisionCanvas.Rendering.Abstractions;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace VisionCanvas.Tests;

public class ImageOverdrawTests
{
    [Fact]
    public void CoveredCoarserLevelsAreSkippedDuringMainImageDraw()
    {
        var scene = Scene(1025, 769);
        var tiles = Tiles(scene);
        var mesh = Draw(scene, tiles);
        Assert.DoesNotContain(mesh.Batches, batch => batch.Texture == tiles[new(2, 0, 0)]);
        Assert.DoesNotContain(mesh.Batches, batch => batch.Texture == tiles[new(1, 0, 0)]);
        Assert.Equal(6, mesh.Batches.Count(batch => batch.Texture != null));
    }

    [Fact]
    public void PartialFineTilesKeepFallbackLevelBehindUncoveredArea()
    {
        var scene = Scene(1025, 769);
        var tiles = Tiles(scene);
        tiles.Remove(new(0, 1, 0));
        var mesh = Draw(scene, tiles);
        Assert.Contains(mesh.Batches, batch => batch.Texture == tiles[new(2, 0, 0)]);
    }

    [Fact]
    public void MissingCoarseTileFallsBackToOverviewWithoutOpaqueSkip()
    {
        var scene = Scene(1025, 769);
        var tiles = Tiles(scene);
        tiles.Remove(new(0, 1, 0));
        tiles.Remove(new(1, 0, 0));
        var mesh = Draw(scene, tiles);
        Assert.Contains(mesh.Batches, batch => batch.Texture == tiles[new(2, 0, 0)]);
    }

    [Fact]
    public void LiveFullResolutionFrameSkipsOverviewBehindMainImage()
    {
        var scene = Scene(1024, 768) with
        {
            IsLive = true
        };
        var overview = Texture(512, 384);
        var live = Texture(1024, 768);
        var mesh = Draw(scene, new Dictionary<TileKey, IRenderTexture>(), live: live,
            liveBounds: new(0, 0, 1024, 768), liveOverview: overview);
        Assert.DoesNotContain(mesh.Batches, batch => batch.Texture == overview);
        Assert.Contains(mesh.Batches, batch => batch.Texture == live);
    }

    [Fact]
    public void LiveSubregionDrawsLiveFrameOnlyOverLiveBounds()
    {
        var scene = Scene(1024, 768) with
        {
            IsLive = true
        };
        var overview = Texture(512, 384);
        var live = Texture(512, 384);
        var mesh = Draw(scene, new Dictionary<TileKey, IRenderTexture>(), live: live,
            liveBounds: new(0, 0, 512, 384), liveOverview: overview);
        Assert.Contains(mesh.Batches, batch => batch.Texture == live);
    }

    [Fact]
    public void LiveFullBoundsButDownsampledTextureDrawsLiveFrame()
    {
        var scene = Scene(1024, 768) with
        {
            IsLive = true
        };
        var overview = Texture(512, 384);
        var live = Texture(512, 384);
        var mesh = Draw(scene, new Dictionary<TileKey, IRenderTexture>(), live: live,
            liveBounds: new(0, 0, 1024, 768), liveOverview: overview);
        Assert.Contains(mesh.Batches, batch => batch.Texture == live);
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
            Document = scene.Document.WithImage(new("test", "test", 1025, 769)),
            MinimapOptions = new(true)
        };
        var tiles = Tiles(scene);
        var painter = new ScenePainter();
        var mesh = Draw(scene, tiles, painter);
        var overview = tiles[new(scene.Image!.Levels - 1, 0, 0)];
        Assert.DoesNotContain(mesh.Batches.Take(painter.RoiBatchBoundary), batch => batch.Texture == overview);
        Assert.Contains(mesh.Batches.Skip(painter.RoiBatchBoundary), batch => batch.Texture == overview);
        mesh = Draw(scene with
        {
            MinimapOptions = new(false)
        }, tiles, painter);
        Assert.DoesNotContain(mesh.Batches.Skip(painter.RoiBatchBoundary), batch => batch.Texture == overview);
    }

    [Fact]
    public void MinimapImageVerticesFollowLayoutAtEveryPositionAndDpi()
    {
        var white = Texture(1, 1);
        var overview = Texture(512, 512);
        foreach (var position in Enum.GetValues<MinimapPosition>())
        {
            foreach (var dpi in new[] { 1.0, 1.5, 2.0 })
            {
                var scene = Scene(8192, 8192) with
                {
                    Document = DocumentState.Empty.WithImage(new("test", "test", 8192, 8192)),
                    Dpi = dpi,
                    MinimapOptions = new(true, position)
                };
                var mesh = new SpriteMesh();
                mesh.Begin(scene.Width, scene.Height);
                MinimapPainter.Draw(mesh, scene, white, overview, _ => white);
                var imageBatch = Assert.Single(mesh.Batches, batch => batch.Texture == overview);
                var bounds = MinimapLayout.Create(8192, 8192, scene.Width, scene.Height, scene.MinimapOptions)!.ImageBounds;
                var first = mesh.Vertices[imageBatch.Start].Position;
                var opposite = mesh.Vertices[imageBatch.Start + 2].Position;
                Assert.Equal((float)((bounds.Left * 2 / scene.Width) - 1), first.X);
                Assert.Equal((float)(1 - (bounds.Top * 2 / scene.Height)), first.Y);
                Assert.Equal((float)((bounds.Right * 2 / scene.Width) - 1), opposite.X);
                Assert.Equal((float)(1 - (bounds.Bottom * 2 / scene.Height)), opposite.Y);
            }
        }
    }

    private static RenderScene Scene(int width, int height)
        => new(DocumentState.Empty.WithViewport(new(1, new(0, 0))),
            new("test", "test", width, height, PixelKind.Gray8, ImageInfo.CalculateLevelCount(width, height)),
            1, 1200, 900, 1, new())
        {
            MinimapOptions = new(false)
        };

    private static Dictionary<TileKey, IRenderTexture> Tiles(RenderScene scene)
    {
        var image = scene.Image!;
        var result = new Dictionary<TileKey, IRenderTexture>();
        for (var level = 0; level < image.Levels; level++)
        {
            var scale = 1 << level;
            var levelWidth = (image.Width + scale - 1) / scale;
            var levelHeight = (image.Height + scale - 1) / scale;
            var columns = (levelWidth + ImageInfo.TileSize - 1) / ImageInfo.TileSize;
            var rows = (levelHeight + ImageInfo.TileSize - 1) / ImageInfo.TileSize;
            for (var y = 0; y < rows; y++)
            {
                for (var x = 0; x < columns; x++)
                {
                    result[new(level, x, y)] = Texture(
                        Math.Min(ImageInfo.TileSize, levelWidth - (x * ImageInfo.TileSize)),
                        Math.Min(ImageInfo.TileSize, levelHeight - (y * ImageInfo.TileSize)));
                }
            }
        }
        return result;
    }

    private static GpuTexture Texture(int width, int height)
        => new(null!, 0, PixelKind.Gray8, width, height, _ => { })
        {
            HasPixels = true,
            IsOpaque = true
        };

    private static SpriteMesh Draw(RenderScene scene, IReadOnlyDictionary<TileKey, IRenderTexture> tiles,
        ScenePainter? painter = null, IRenderTexture? live = null, BoundsD liveBounds = default, IRenderTexture? liveOverview = null)
    {
        var white = Texture(1, 1);
        using var filters = new GpuFilterPreview();
        return (painter ?? new ScenePainter()).Build(scene, white, tiles, _ => white, _ => null, live, liveBounds, filters, liveOverview);
    }
}
