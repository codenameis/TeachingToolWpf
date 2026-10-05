using VisionCanvas.Core.Geometry;
using VisionGeometry;

namespace TeachingToolWpf.Core.Tests;

public class RoiPlacementTests
{
    [Fact]
    public void AddedRoiIsInsideTheVisibleImageAtHighZoom()
    {
        var shape = RoiPlacement.InView(8192, 8192, new(4, new(-8000, -12000)), 1200, 800);
        Assert.Equal(new PointD(2150, 3100), shape.Center);
        Assert.All(shape.FrameCorners, p =>
        {
            Assert.InRange(p.X, 2000, 2300);
            Assert.InRange(p.Y, 3000, 3200);
        });
    }

    [Fact]
    public void BlankViewportFallsBackToTheImageCenter()
    {
        var shape = RoiPlacement.InView(24576, 1000, new(1, new(1000, 1000)), 800, 600);
        Assert.Equal(new PointD(12288, 500), shape.Center);
        Assert.All(shape.FrameCorners, p =>
        {
            Assert.InRange(p.X, 0, 24576);
            Assert.InRange(p.Y, 0, 1000);
        });
    }

    [Fact]
    public void TinyImageStillGetsAValidRectangle()
    {
        var shape = RoiPlacement.InView(1, 1, ViewportTransform.Identity, 800, 600);
        Assert.True(shape.Width > 0 && shape.Height > 0);
        Assert.All(shape.FrameCorners, p =>
        {
            Assert.InRange(p.X, 0, 1);
            Assert.InRange(p.Y, 0, 1);
        });
    }
}
