using System.Numerics;
using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Interaction;
using VisionCanvas.Layers;
using VisionCanvas.Rendering;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace VisionCanvas.Tests;

public sealed class DrawingLayerRenderingTests
{
    private static readonly GpuTexture s_white = new(null!, 0, PixelKind.Rgba32, 1, 1, _ => { });

    [Theory]
    [InlineData(1, 10, 15, 200, 100, 1)]
    [InlineData(2, -20, 35, 400, 300, 1.5)]
    public void ImageCrosshairTracksImageCenterAndSpansCanvas(double scale, double x, double y, double width, double height, double dpi)
    {
        var layers = new DrawingLayerCollection(() => { }, () => { });
        var layer = layers.Create("axes", LayerSpace.Image);
        layer.Add(new ImageCrosshairGraphic(Vector4.One, 2));
        var scene = Scene(width, height, dpi) with
        {
            Image = new("test", "test", 120, 60, PixelKind.Gray8, 1),
            Document = new EditorSession().State with
            {
                Viewport = new(scale, new(x, y))
            },
            Layers = layers.Snapshot
        };
        var vertices = Draw(scene).Vertices.Select(v => Pixel(v, scene)).ToArray();
        Assert.Equal(12, vertices.Length);
        var horizontal = vertices.Take(6).ToArray();
        var vertical = vertices.Skip(6).ToArray();
        Assert.Equal(0, horizontal.Min(p => p.X), 3);
        Assert.Equal(width, horizontal.Max(p => p.X), 3);
        Assert.Equal((30 * scale) + y - 1, horizontal.Min(p => p.Y), 3);
        Assert.Equal(0, vertical.Min(p => p.Y), 3);
        Assert.Equal(height, vertical.Max(p => p.Y), 3);
        Assert.Equal((60 * scale) + x - 1, vertical.Min(p => p.X), 3);
        Assert.Empty(Draw(scene with
        {
            Image = null
        }).Vertices);
        var offscreen = scene with
        {
            Document = scene.Document with
            {
                Viewport = new(1, new(-300, 0))
            }
        };
        Assert.Equal(6, Draw(offscreen).Vertices.Count); // 화면 밖 세로 축은 숨기고 가로 축만 유지한다.
        Assert.Throws<ArgumentException>(() => layers.Create("screen axes", LayerSpace.Screen).Add(new ImageCrosshairGraphic(Vector4.One)));
    }

    [Theory]
    [InlineData(1, 0, 0, 200, 100, 1)]
    [InlineData(3, -20, 45, 400, 300, 1.5)]
    public void ImageCoordinatesMoveButScreenCrosshairStaysCentered(double scale, double x, double y, double width, double height, double dpi)
    {
        var layers = new DrawingLayerCollection(() => { }, () => { });
        var image = layers.Create("image", LayerSpace.Image);
        image.Add(new LineGraphic(new(20, 10), new(30, 10), new(1, 0, 0, 1), 2));
        var screen = layers.Create("screen", LayerSpace.Screen);
        screen.Add(CrosshairGraphic.ScreenCenter(new(0, 1, 0, 1)));
        var scene = Scene(width, height, dpi) with
        {
            Document = new EditorSession().State with
            {
                Viewport = new(scale, new(x, y))
            },
            Layers = layers.Snapshot
        };
        var mesh = Draw(scene);
        var imageVertices = mesh.Vertices.Where(v => v.Color.X == 1).Select(v => Pixel(v, scene)).ToArray();
        Assert.Equal((20 * scale) + x, imageVertices.Min(p => p.X), 3);
        Assert.Equal((10 * scale) + y - 1, imageVertices.Min(p => p.Y), 3);
        Assert.Equal(2, imageVertices.Max(p => p.Y) - imageVertices.Min(p => p.Y), 3);
        var guide = mesh.Vertices.Where(v => v.Color.Y == 1).Select(v => Pixel(v, scene)).ToArray();
        Assert.Equal((width / 2) - 16, guide.Min(p => p.X), 3);
        Assert.Equal((width / 2) + 16, guide.Max(p => p.X), 3);
        Assert.Equal((height / 2) - 16, guide.Min(p => p.Y), 3);
        Assert.Equal((height / 2) + 16, guide.Max(p => p.Y), 3);
    }

    [Fact]
    public void HiddenAndClearedLayersDoNotDrawAndCreationOrderIsStable()
    {
        var layers = new DrawingLayerCollection(() => { }, () => { });
        var first = layers.Create("first", LayerSpace.Screen);
        var second = layers.Create("second", LayerSpace.Screen);
        first.Add(new LineGraphic(new(0, 20), new(100, 20), new(1, 0, 0, 1)));
        second.Add(new LineGraphic(new(0, 20), new(100, 20), new(0, 1, 0, 1)));
        var scene = Scene();
        Assert.Equal(new Vector4(0, 1, 0, 1), Draw(scene with
        {
            Layers = layers.Snapshot
        }).Vertices[^1].Color);
        second.IsVisible = false;
        Assert.Equal(new Vector4(1, 0, 0, 1), Draw(scene with
        {
            Layers = layers.Snapshot
        }).Vertices[^1].Color);
        first.Clear();
        Assert.Empty(Draw(scene with
        {
            Layers = layers.Snapshot
        }).Vertices);
        Assert.Single(second.Snapshot.Items);
    }

    [Fact]
    public void ShapeOutlineIsPreparedOnceAndTextKeepsDipSize()
    {
        var layers = new DrawingLayerCollection(() => { }, () => { });
        var layer = layers.Create("results", LayerSpace.Image);
        var shape = RoiGeometry.Polygon([new(20, 20), new(80, 20), new(40, 40), new(80, 80), new(20, 80)]);
        layer.Add(new ShapeGraphic(shape, Vector4.One));
        layer.Add(new TextGraphic(new(20, 20), "한글", new(1, 0, 0, 1)));
        var before = layers.Snapshot;
        var outline = before[0].Items.Values.First().Outline;
        var scene = Scene() with
        {
            Layers = before,
            Dpi = 2
        };
        var mesh = Draw(scene);
        Assert.Equal((5 * 6) + 6, mesh.Vertices.Count);
        var text = mesh.Vertices.Where(v => v.Color.Y == 0).Select(v => Pixel(v, scene)).ToArray();
        Assert.Equal(20, text.Max(p => p.X) - text.Min(p => p.X), 3);
        Assert.Equal(6, text.Max(p => p.Y) - text.Min(p => p.Y), 3);
        _ = Draw(scene with
        {
            Document = scene.Document with
            {
                Viewport = new(2, new(1, 2))
            }
        });
        Assert.Equal(outline, layers.Snapshot[0].Items.Values.First().Outline);
        Assert.Equal(before, layers.Snapshot);
    }

    [Fact]
    public void OffscreenLongLineIsClippedWithoutExtraGeometry()
    {
        var layers = new DrawingLayerCollection(() => { }, () => { });
        var layer = layers.Create("lines", LayerSpace.Image);
        layer.Add(new LineGraphic(new(-1e12, 20), new(1e12, 20), Vector4.One));
        layer.Add(new LineGraphic(new(-100, -100), new(-50, -100), Vector4.One));
        var scene = Scene() with
        {
            Layers = layers.Snapshot
        };
        var mesh = Draw(scene);
        Assert.Equal(6, mesh.Vertices.Count);
        Assert.All(mesh.Vertices, vertex => Assert.InRange(Pixel(vertex, scene).X, -.51, 200.51));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OnlyActiveMouseAreaGetsOneTranslucentQuad(bool drawing)
    {
        var scene = Scene();
        var area = new BoundsD(10, 20, 80, 90);
        scene = scene with
        {
            Overlay = drawing ? new()
            {
                DragArea = area
            } : new(Marquee: area)
        };
        var mesh = new SpriteMesh();
        mesh.Begin(scene.Width, scene.Height);
        ScenePainter.DrawDragFill(mesh, scene, s_white);
        Assert.Equal(6, mesh.Vertices.Count);
        Assert.All(mesh.Vertices, vertex => Assert.Equal(.08f, vertex.Color.W));
        var corners = mesh.Vertices.Select(v => Pixel(v, scene)).ToArray();
        Assert.Equal(10, corners.Min(p => p.X), 3);
        Assert.Equal(90, corners.Max(p => p.Y), 3);
        mesh.Begin(scene.Width, scene.Height);
        ScenePainter.DrawDragFill(mesh, scene with
        {
            Overlay = new()
        }, s_white);
        Assert.Empty(mesh.Vertices);
    }

    private static RenderScene Scene(double width = 200, double height = 200, double dpi = 1)
        => new(new EditorSession().State, null, 0, width, height, dpi, new());

    private static PointD Pixel(SpriteVertex vertex, RenderScene scene)
        => new((vertex.Position.X + 1) * scene.Width / 2, (1 - vertex.Position.Y) * scene.Height / 2);

    private static SpriteMesh Draw(RenderScene scene)
    {
        var mesh = new SpriteMesh();
        mesh.Begin(scene.Width, scene.Height);
        var text = new GpuTexture(null!, 1, PixelKind.Rgba32, 40, 12, _ => { });
        DrawingLayerPainter.Draw(mesh, scene, s_white, _ => text, _ => text);
        return mesh;
    }
}
