using TeachingToolWpf.Core.Geometry;
using TeachingToolWpf.Rendering;

namespace TeachingToolWpf.Imaging.Tests;

public sealed class OverlayStrokeGeometryTests
{
    [Fact]
    public void DashPhaseContinuesAroundCornersAndClosingEdge()
    {
        PointD[] polygon = [new(10, 10), new(30, 10), new(30, 30), new(10, 30)];
        var segments = OverlayStrokeGeometry.ClosedOutline(polygon, new(0, 0, 50, 50), 8, 5).ToArray();

        Assert.Contains(new OverlayStrokeSegment(new(10, 10), new(18, 10)), segments);
        Assert.Contains(new OverlayStrokeSegment(new(23, 10), new(30, 10)), segments);
        Assert.Contains(new OverlayStrokeSegment(new(30, 10), new(30, 11)), segments);
        Assert.Contains(new OverlayStrokeSegment(new(10, 25), new(10, 17)), segments);
        Assert.Contains(new OverlayStrokeSegment(new(10, 12), new(10, 10)), segments);
    }

    [Fact]
    public void HugeOffscreenEdgesAreClippedBeforeDashExpansionAndPreservePhase()
    {
        PointD[] polygon = [new(-1e9, 60), new(1e9, 60), new(1e9, 160), new(-1e9, 160)];
        var clip = new BoundsD(0, 0, 200, 200);
        var segments = OverlayStrokeGeometry.ClosedOutline(polygon, clip, 8, 5).ToArray();

        Assert.InRange(segments.Length, 30, 34);
        Assert.Equal(1, segments[0].Start.X, 5);
        Assert.Equal(9, segments[0].End.X, 5);
        Assert.Equal(60, segments[0].Start.Y);
        Assert.All(segments, segment =>
        {
            Assert.True(segment.Start.IsFinite && segment.End.IsFinite);
            Assert.InRange(segment.Start.X, -.000001, 200.000001);
            Assert.InRange(segment.End.X, -.000001, 200.000001);
            Assert.InRange((segment.End - segment.Start).Length, 0, 8.000001);
        });
    }

    [Fact]
    public void SolidClippingRetainsPolygonEdgesWithoutInventingViewportEdges()
    {
        PointD[] polygon = [new(-100, 60), new(100, 60), new(100, 160), new(-100, 160)];
        var segments = OverlayStrokeGeometry.ClosedOutline(polygon, new(0, 0, 200, 200)).ToArray();

        Assert.Equal(new OverlayStrokeSegment[]
        {
            new(new(0, 60), new(100, 60)),
            new(new(100, 60), new(100, 160)),
            new(new(100, 160), new(0, 160))
        }, segments);
    }
}
