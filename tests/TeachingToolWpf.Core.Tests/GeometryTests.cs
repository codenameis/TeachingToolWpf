using TeachingToolWpf.Core.Geometry;

namespace TeachingToolWpf.Core.Tests;

public sealed class GeometryTests
{
    [Fact]
    public void RotatedContainmentRejectsCornerOutsideDespiteAabbOverlap()
    {
        var parent = new RoiGeometry(new PointD(100, 100), 100, 20, 45);
        var child = new RoiGeometry(new PointD(100, 120), 10, 10, 0);
        Assert.False(parent.Contains(child));
        Assert.True(parent.Contains(new RoiGeometry(new PointD(100, 100), 10, 5, 45)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(359.9)]
    public void LocalWorldRoundTripPreservesLargeCoordinates(double angle)
    {
        var rect = new RoiGeometry(new PointD(24576, 131072), 300, 200, angle);
        var p = new PointD(13.25, -4.5);
        Assert.True((rect.ToLocal(rect.ToWorld(p)) - p).Length < 1e-8);
    }

    [Fact]
    public void ZoomPreservesImagePointUnderCursor()
    {
        var view = new ViewportTransform(0.25, new PointD(20, 30));
        var anchor = new PointD(500, 320);
        Assert.True((view.ScreenToImage(anchor) - view.ZoomAt(anchor, 2).ScreenToImage(anchor)).Length < 1e-9);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    public void InvalidSizesAreRejected(double width) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new RoiGeometry(new PointD(0, 0), width, 10, 0));
}
