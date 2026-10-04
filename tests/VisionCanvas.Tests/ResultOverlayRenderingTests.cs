using System.Collections.Immutable;
using System.Numerics;
using TeachingToolWpf.Core.Editing;
using TeachingToolWpf.Core.Geometry;
using TeachingToolWpf.Core.Imaging;
using TeachingToolWpf.Rendering;

namespace TeachingToolWpf.Imaging.Tests;

public sealed class ResultOverlayRenderingTests
{
    [Theory]
    [InlineData(.25, 1)]
    [InlineData(2, 1.5)]
    public void SearchBoundaryUsesScreenDipDashesAndGaps(double scale, double dpi)
    {
        var color = new Vector4(0, 1, 0, 1);
        var view = new ViewportTransform(scale, new(10, 20));
        var screen = new PointD[] { new(20, 60), new(180, 60), new(180, 180), new(20, 180) };
        var scene = Scene(new("status", color, screen.Select(view.ScreenToImage).ToImmutableArray(), []));
        scene = scene with
        {
            Document = scene.Document with
            {
                Viewport = view
            },
            Dpi = dpi
        };
        var mesh = Draw(scene, []);

        Assert.True(Covers(mesh, new(24, 60), color));
        Assert.False(Covers(mesh, new(30, 60), color));
        Assert.True(Covers(mesh, new(35, 60), color));
        Assert.False(Covers(mesh, new(43, 60), color));
    }

    [Theory]
    [InlineData(.25, 1)]
    [InlineData(2, 1.5)]
    public void ConcaveDetectionRetainsEverySolidEdgeIncludingClosingEdge(double scale, double dpi)
    {
        var color = new Vector4(0, 1, 0, 1);
        ImmutableArray<PointD> points = [new(60, 70), new(150, 70), new(110, 110), new(150, 160), new(60, 160)];
        var view = new ViewportTransform(scale, new(10, 20));
        var marker = new MatchMarker(points.Select(view.ScreenToImage).ToImmutableArray(), view.ScreenToImage(new(-100, -100)), 0, "pose", color, []);
        var scene = Scene(new("status", Vector4.One, [], [marker]));
        scene = scene with
        {
            Document = scene.Document with
            {
                Viewport = view
            },
            Dpi = dpi
        };
        var mesh = Draw(scene, []);

        for (var i = 0; i < points.Length; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % points.Length];
            for (var fraction = .1; fraction < 1; fraction += .1)
            {
                Assert.True(Covers(mesh, a + ((b - a) * fraction), color));
            }
        }
        Assert.False(Covers(mesh, new(150, 110), color));
        Assert.False(Covers(mesh, new(80, 72), color));
    }

    [Fact]
    public void MatchLabelsStayOutsideOwnFootprintAndCannotHideAnotherOutline()
    {
        var color = new Vector4(0, 1, 0, 1);
        var hiddenLabel = new MatchMarker([new(90, 60), new(160, 60), new(160, 160), new(90, 160)], new(-100, -100), 0, "hidden", color, []);
        var visibleLabel = new MatchMarker([new(60, 80), new(100, 80), new(100, 100), new(60, 100)], new(80, 90), 0, "short", color, []);
        var mesh = Draw(Scene(new("status", Vector4.One, [], [hiddenLabel, visibleLabel])), []);
        var textBatch = Assert.Single(mesh.Batches, batch => batch.Texture.Width == 40 && batch.Start < 20);
        var textTopLeft = mesh.Vertices[textBatch.Start].Position;

        Assert.Equal(66, (textTopLeft.X + 1) * 100, .001);
        Assert.Equal(58, (1 - textTopLeft.Y) * 100, .001);
        Assert.Equal(color, LastColorAt(mesh, new(100, 60)));
    }

    [Theory]
    [InlineData(50, 205, 50)]
    [InlineData(255, 99, 71)]
    [InlineData(255, 165, 0)]
    [InlineData(255, 215, 0)]
    public void StatusWithoutMatchesStillRendersHeaderAndSearchBoundary(int red, int green, int blue)
    {
        var color = new Vector4(red / 255f, green / 255f, blue / 255f, 1);
        var labels = new List<string>();
        var scene = Scene(new("status", color, [new(10, 50), new(110, 50), new(110, 150), new(10, 150)], []));
        var mesh = Draw(scene, labels);
        Assert.Contains("status", labels);
        Assert.Contains(mesh.Vertices, vertex => vertex.Color == color && Math.Abs(vertex.Position.X + .9) < .01);
    }

    [Fact]
    public void FeatureVisibilityChangesOnlyVisiblePointGeometryAndPreservesPose()
    {
        var color = new Vector4(1, .3f, .2f, 1);
        var marker = new MatchMarker([new(80, 80), new(120, 80), new(120, 120), new(80, 120)], new(100, 100), .5, "pose", color,
            [new(90, 90), new(105, 95), new(9999, 9999)]);
        var overlay = new ResultOverlay("status", color, [], [marker]);
        var hidden = Draw(Scene(overlay), []);
        var shown = Draw(Scene(overlay with
        {
            ShowFeatures = true
        }), []);
        Assert.Equal(hidden.Vertices.Count + 12, shown.Vertices.Count);
        Assert.All(hidden.Vertices, vertex => Assert.Contains(vertex, shown.Vertices));
        Assert.Empty(Draw(Scene(null), []).Vertices);
    }

    private static RenderScene Scene(ResultOverlay? result)
        => new(new EditorSession().State, null, 1, 200, 200, 1, new(null, null, null), Inspection: result);

    private static SpriteMesh Draw(RenderScene scene, List<string> labels)
    {
        var white = new GpuTexture(null!, 0, PixelKind.Rgba32, 1, 1, _ => { });
        var text = new GpuTexture(null!, 1, PixelKind.Rgba32, 40, 12, _ => { });
        var mesh = new SpriteMesh();
        mesh.Begin(scene.Width, scene.Height);
        ResultOverlayPainter.Draw(mesh, scene, white, value => { labels.Add(value); return text; }, _ => text);
        return mesh;
    }

    private static bool Covers(SpriteMesh mesh, PointD point, Vector4 color)
        => ColorsAt(mesh, point).Contains(color);

    private static Vector4 LastColorAt(SpriteMesh mesh, PointD point) => ColorsAt(mesh, point).Last();

    private static IEnumerable<Vector4> ColorsAt(SpriteMesh mesh, PointD point)
    {
        var target = new Vector2((float)((point.X / 100) - 1), (float)(1 - (point.Y / 100)));
        for (var index = 0; index < mesh.Vertices.Count; index += 3)
        {
            var a = mesh.Vertices[index];
            var b = mesh.Vertices[index + 1];
            var c = mesh.Vertices[index + 2];
            var first = Cross(b.Position - a.Position, target - a.Position);
            var second = Cross(c.Position - b.Position, target - b.Position);
            var third = Cross(a.Position - c.Position, target - c.Position);
            if ((first >= -.00001 && second >= -.00001 && third >= -.00001) ||
                (first <= .00001 && second <= .00001 && third <= .00001))
            {
                yield return a.Color;
            }
        }
    }

    private static double Cross(Vector2 a, Vector2 b) => (a.X * b.Y) - (a.Y * b.X);
}
