using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Geometry;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Core.Tests;

public class RotationRangeTests
{
    [Fact]
    public void RotatedBoundsMatchActualRotatedCornersInsteadOfRotatedBoundingBoxes()
    {
        var random = new Random(710);
        var rois = Enumerable.Range(0, 200).Select(i => RoiAt(new(random.Next(100, 900), random.Next(100, 700)),
            random.Next(5, 100), random.Next(5, 100), random.Next(360), i)).ToArray();
        var index = new RoiSpatialIndex(rois);
        var pivot = new PointD(413, 367);
        foreach (var angle in new[] { 0d, 15, 37, -89, 180, 359, 721 })
        {
            var expected = BoundsD.FromPoints(rois.SelectMany(r => r.Geometry.RotateAround(pivot, angle).FrameCorners));
            AssertBounds(expected, index.RotatedBounds(pivot, angle), 1e-8);
        }
        var thin = RoiAt(new(500, 500), 180, 4, 45);
        var thinIndex = new RoiSpatialIndex([thin]);
        var actual = thinIndex.RotatedBounds(thin.Geometry.Center, -45);
        Assert.Equal(180, actual.Width, 8);
        Assert.Equal(4, actual.Height, 8);
    }

    [Fact]
    public void FirstExitStopsBeforeInvalidMiddleEvenWhenHalfTurnFitsAgain()
    {
        var roi = RoiAt(new(100, 50), 180, 20, 0);
        var index = new RoiSpatialIndex([roi]);
        var image = new ImageReference("test", "test", 200, 100);
        var range = index.RotationLimits(roi.Geometry.Center, image);
        Assert.True(Fits([roi], roi.Geometry.Center, 180, image));
        Assert.False(Fits([roi], roi.Geometry.Center, 90, image));
        Assert.InRange(range.MaximumDegrees, 25, 30);
        Assert.InRange(range.MinimumDegrees, -30, -25);
        Assert.Equal(range.MaximumDegrees, range.Clamp(180));
        Assert.Equal(range.MinimumDegrees, range.Clamp(-180));
        Assert.True(Fits([roi], roi.Geometry.Center, range.MaximumDegrees - 1e-6, image));
        Assert.False(Fits([roi], roi.Geometry.Center, range.MaximumDegrees + .001, image));
        Assert.True(Fits([roi], roi.Geometry.Center, range.MinimumDegrees + 1e-6, image));
        Assert.False(Fits([roi], roi.Geometry.Center, range.MinimumDegrees - .001, image));
    }

    [Fact]
    public void WholeCircleInsideImageDoesNotClampMultipleTurns()
    {
        var roi = RoiAt(new(100, 100), 100, 100, 27);
        var index = new RoiSpatialIndex([roi]);
        var range = index.RotationLimits(roi.Geometry.Center, new("test", "test", 200, 200));
        Assert.Equal(double.NegativeInfinity, range.MinimumDegrees);
        Assert.Equal(double.PositiveInfinity, range.MaximumDegrees);
        Assert.Equal(1080, range.Clamp(1080));
        Assert.Equal(-721, range.Clamp(-721));
    }

    [Fact]
    public void TouchingBoundaryCannotJumpAcrossImmediateViolation()
    {
        var roi = RoiAt(new(50, 10), 80, 20, 0);
        var index = new RoiSpatialIndex([roi]);
        var range = index.RotationLimits(roi.Geometry.Center, new("test", "test", 100, 100));
        Assert.InRange(range.MaximumDegrees, 0, .00001);
        Assert.InRange(range.MinimumDegrees, -.00001, 0);
        Assert.Equal(range.MaximumDegrees, range.Clamp(360));
        Assert.Equal(range.MinimumDegrees, range.Clamp(-360));
    }

    [Fact]
    public void LimitsCoverEveryIntermediateAngleAndStopAtAnActualCorner()
    {
        var random = new Random(139);
        var image = new ImageReference("test", "test", 1000, 800);
        var pivot = new PointD(500, 400);
        for (var sample = 0;
sample < 12;
sample++)
        {
            var rois = Enumerable.Range(0, 100).Select(i => RoiAt(new(random.Next(40, 960), random.Next(40, 760)),
                random.Next(5, 30), random.Next(5, 30), random.Next(360), i)).ToArray();
            var range = new RoiSpatialIndex(rois).RotationLimits(pivot, image);
            Assert.True(double.IsFinite(range.MinimumDegrees));
            Assert.True(double.IsFinite(range.MaximumDegrees));
            foreach (var boundary in new[] { range.MinimumDegrees, range.MaximumDegrees })
            {
                if (!double.IsFinite(boundary))
                {
                    continue;
                }

                Assert.True(Fits(rois, pivot, boundary, image));
                for (var i = 0;
i < 20;
i++)
                {
                    Assert.True(Fits(rois, pivot, boundary * i / 20, image));
                }

                Assert.True(Fits(rois, pivot, boundary - (Math.Sign(boundary) * 1e-6), image));
                Assert.False(Fits(rois, pivot, boundary + (Math.Sign(boundary) * .001), image));
            }
        }
    }

    [Fact]
    public void GeometryUpdatesKeepPriorRotationBoundsAndLimitsIndependent()
    {
        var roi = RoiAt(new(100, 100), 100, 100, 0);
        var before = new RoiSpatialIndex([roi]);
        var image = new ImageReference("test", "test", 200, 200);
        var pivot = new PointD(100, 100);
        var changed = roi with
        {
            Geometry = new(new(100, 100), 180, 180, 0)
        };
        var after = before.WithChanges([(roi.Id, changed)]);
        Assert.Equal(double.PositiveInfinity, before.RotationLimits(pivot, image).MaximumDegrees);
        Assert.InRange(after.RotationLimits(pivot, image).MaximumDegrees, 0, 15);
        AssertBounds(new(50, 50, 150, 150), before.RotatedBounds(pivot, 0), 0);
        AssertBounds(new(10, 10, 190, 190), after.RotatedBounds(pivot, 0), 0);
    }

    [Fact]
    public void LargeImageCoordinatesUsePivotRelativeProjection()
    {
        var pivot = new PointD(1_000_000_000, 1_000_000_000);
        var rois = new[] { RoiAt(pivot + new PointD(70, -50), 15, 29, 17), RoiAt(pivot + new PointD(-30, 20), 11, 8, 75) };
        var index = new RoiSpatialIndex(rois);
        foreach (var angle in new[] { -179d, 35, 91 })
        {
            AssertBounds(BoundsD.FromPoints(rois.SelectMany(r => r.Geometry.RotateAround(pivot, angle).FrameCorners)), index.RotatedBounds(pivot, angle), 1e-6);
        }

        Assert.Equal(RotationRange.Unbounded, index.RotationLimits(pivot, new("large", "large", 2_000_000_000, 2_000_000_000)));
    }

    [Fact]
    public void EmptySelectionAndInvalidArgumentsHaveExplicitContracts()
    {
        var empty = new RoiSpatialIndex([]);
        var image = new ImageReference("test", "test", 100, 100);
        Assert.Equal(default, empty.RotatedBounds(new(50, 50), 30));
        Assert.Equal(RotationRange.Unbounded, empty.RotationLimits(new(50, 50), image));
        Assert.Throws<ArgumentOutOfRangeException>(() => empty.RotatedBounds(new(double.NaN, 0), 30));
        Assert.Throws<ArgumentOutOfRangeException>(() => empty.RotatedBounds(default, double.PositiveInfinity));
        Assert.Throws<ArgumentOutOfRangeException>(() => RotationRange.Unbounded.Clamp(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RotationRange(1, 2));
        Assert.Throws<OperationCanceledException>(() => empty.RotationLimits(default, image, new CancellationToken(true)));
    }

    [Fact]
    public void TangencyAllowsFullCircleButAnExtremelyShortForbiddenArcDoesNot()
    {
        var pivot = new PointD(50, 50);
        var image = new ImageReference("test", "test", 100, 100);
        var tangent = RoiAt(pivot, 60, 80, 0);
        Assert.Equal(RotationRange.Unbounded, new RoiSpatialIndex([tangent]).RotationLimits(pivot, image));
        var crossing = RoiAt(pivot, 60, 80.0001, 0);
        var range = new RoiSpatialIndex([crossing]).RotationLimits(pivot, image);
        Assert.InRange(range.MaximumDegrees, 30, 40);
        Assert.True(Fits([crossing], pivot, range.MaximumDegrees, image));
        Assert.False(Fits([crossing], pivot, Math.Atan2(30, 40.00005) * 180 / Math.PI, image));
    }

    [Fact]
    public void ClampedEndpointStaysInsideAtLargeWorldCoordinates()
    {
        var pivot = new PointD(1_000_000_000, 500_000_000);
        var image = new ImageReference("large", "large", 2_000_000_000, 1_000_000_000);
        var roi = RoiAt(pivot, 1_800_000_000, 200_000_000, 0);
        var range = new RoiSpatialIndex([roi]).RotationLimits(pivot, image);
        Assert.InRange(range.MaximumDegrees, 25, 30);
        Assert.True(Fits([roi], pivot, range.MaximumDegrees, image));
        Assert.True(Fits([roi], pivot, range.MinimumDegrees, image));
    }

    private static Roi RoiAt(PointD center, double width, double height, double angle, long order = 0)
        => new(Guid.NewGuid(), "roi", new(center, width, height, angle), null, false, order);

    private static bool Fits(IEnumerable<Roi> rois, PointD pivot, double angle, ImageReference image)
        => rois.SelectMany(r => r.Geometry.RotateAround(pivot, angle).FrameCorners)
            .All(p => p.X >= -1e-7 && p.Y >= -1e-7 && p.X <= image.Width + 1e-7 && p.Y <= image.Height + 1e-7);

    private static void AssertBounds(BoundsD expected, BoundsD actual, double tolerance)
    {
        Assert.InRange(Math.Abs(expected.Left - actual.Left), 0, tolerance);
        Assert.InRange(Math.Abs(expected.Top - actual.Top), 0, tolerance);
        Assert.InRange(Math.Abs(expected.Right - actual.Right), 0, tolerance);
        Assert.InRange(Math.Abs(expected.Bottom - actual.Bottom), 0, tolerance);
    }
}
