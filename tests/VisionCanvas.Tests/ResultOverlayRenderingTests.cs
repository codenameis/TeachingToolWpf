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
}
