using VisionGeometry;

namespace TeachingToolWpf.Core.Tests;

public sealed class FiducialAlignmentTests
{
    [Fact]
    public void OneMarkTranslatesWithoutChangingRoiShapeOrSize()
    {
        Assert.True(FiducialAlignment.TryCreate([new(10, 20)], [new(13, 18)], out var alignment, out var error), error);
        var roi = new RoiGeometry(new(100, 200), 30, 40, 27, RoiShape.Triangle);
        var mapped = alignment!.Apply(roi);

        Assert.Equal(new PointD(103, 198), mapped.Center);
        Assert.Equal(27, mapped.Angle);
        Assert.Equal(roi.Width, mapped.Width);
        Assert.Equal(roi.Height, mapped.Height);
        Assert.Same(roi.Shape, mapped.Shape);
        Assert.Equal(new PointD(100, 200), roi.Center);
        Assert.Equal(0, alignment.MaxErrorPixels);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void MultipleMarksRecoverKnownRotationAndTranslationAt32K(int count)
    {
        PointD[] reference = [new(120, 300), new(17000, 300), new(17000, 30000), new(120, 30000)];
        var detected = reference.Select(p => new PointD((0.6 * p.X) - (0.8 * p.Y) + 35, (0.8 * p.X) + (0.6 * p.Y) - 12)).ToArray();

        Assert.True(FiducialAlignment.TryCreate(reference.AsSpan(0, count), detected.AsSpan(0, count), out var alignment, out var error), error);

        var mapped = alignment!.Transform.Apply(new(500, 1000));
        AssertClose(new(-465, 988), mapped);
        AssertClose(new(500, 1000), alignment.Transform.InverseApply(mapped));
        Assert.InRange(alignment.MaxErrorPixels, 0, 1e-8);
        Assert.Equal(count, alignment.MarkCount);
    }

    [Fact]
    public void CollinearMarksAreValidForRigidAlignment()
    {
        Assert.True(FiducialAlignment.TryCreate([new(0, 0), new(100, 0), new(200, 0)],
            [new(10, 20), new(10, 120), new(10, 220)], out var alignment, out var error), error);
        AssertClose(new(-40, 45), alignment!.Transform.Apply(new(25, 50)));
    }

    [Fact]
    public void FourMarksUseAllMeasurementsAndReportResiduals()
    {
        PointD[] reference = [new(0, 0), new(100, 0), new(100, 100), new(0, 100)];
        PointD[] detected = [new(10, 20), new(110, 20), new(110, 120), new(13, 120)];
        Assert.True(FiducialAlignment.TryCreate(reference, detected, out var alignment, out var error, maximumErrorPixels: 5), error);

        AssertClose(new(60.75, 70), alignment!.Transform.Apply(new(50, 50)));
        Assert.True(alignment.MaxErrorPixels > alignment.RmsErrorPixels);
        var limit = (alignment.MaxErrorPixels + alignment.RmsErrorPixels) / 2;
        Assert.False(FiducialAlignment.TryCreate(reference, detected, out var rejected, out error, maximumErrorPixels: limit));
        Assert.Null(rejected);
        Assert.Contains("오차", error);
    }

    [Fact]
    public void ChildOneMarkPreservesParentRotationAndAddsOnlyItsResidual()
    {
        Assert.True(FiducialAlignment.TryCreate([new(0, 0), new(100, 0)], [new(100, 200), new(100, 300)], out var parent, out _));
        Assert.True(FiducialAlignment.TryCreate([new(20, 30)], [new(75, 227)], out var child, out var error, parent: parent), error);

        Assert.Equal(0, child!.LocalTransform.AngleDegrees);
        Assert.Equal(new PointD(5, 7), child.LocalTransform.Translation);
        AssertClose(new(65, 237), child.Transform.Apply(new(30, 40)));
        AssertClose(child.Transform.Apply(new(30, 40)), child.LocalTransform.Apply(parent!.Transform.Apply(new(30, 40))));
        Assert.Same(parent, child.Parent);
    }

    [Fact]
    public void ChildTwoMarksComposeRotationWithoutApplyingParentTwice()
    {
        Assert.True(FiducialAlignment.TryCreate([new(0, 0), new(100, 0)], [new(100, 200), new(100, 300)], out var parent, out _));
        Assert.True(FiducialAlignment.TryCreate([new(20, 30), new(30, 30)], [new(78, 229), new(68, 229)],
            out var child, out var error, parent: parent), error);

        AssertClose(new(73, 224), child!.Transform.Apply(new(25, 35)));
        AssertClose(child.Transform.Apply(new(25, 35)), child.LocalTransform.Apply(parent!.Transform.Apply(new(25, 35))));
    }

    [Fact]
    public void CallerArraysAreNotRetained()
    {
        PointD[] reference = [new(1, 2)];
        PointD[] detected = [new(4, 6)];
        Assert.True(FiducialAlignment.TryCreate(reference, detected, out var alignment, out _));
        reference[0] = new(1000, 1000);
        detected[0] = new(-1000, -1000);
        Assert.Equal(new PointD(13, 24), alignment!.Transform.Apply(new(10, 20)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void MarkCountOutsideOneToFourIsRejected(int count)
    {
        Assert.False(FiducialAlignment.TryCreate(new PointD[count], new PointD[count], out var alignment, out var error));
        Assert.Null(alignment);
        Assert.NotEmpty(error);
    }

    [Fact]
    public void MismatchedMarkCountsAreRejected()
    {
        Assert.False(FiducialAlignment.TryCreate([new(0, 0)], [new(0, 0), new(100, 0)], out var alignment, out _));
        Assert.Null(alignment);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NonFiniteMarksAreRejected(double value)
    {
        Assert.False(FiducialAlignment.TryCreate([new(value, 0)], [new(0, 0)], out _, out _));
        Assert.False(FiducialAlignment.TryCreate([new(0, 0)], [new(0, value)], out _, out _));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidErrorLimitsAreRejected(double limit)
    {
        Assert.False(FiducialAlignment.TryCreate([new(0, 0)], [new(0, 0)], out _, out _, maximumErrorPixels: limit));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0.5)]
    public void DuplicateOrSubpixelSeparatedMarksAreRejected(double distance)
    {
        PointD[] close = [new(0, 0), new(distance, 0)];
        PointD[] spread = [new(0, 0), new(100, 0)];
        Assert.False(FiducialAlignment.TryCreate(close, spread, out _, out _));
        Assert.False(FiducialAlignment.TryCreate(spread, close, out _, out _));
    }

    [Fact]
    public void ScaleChangeIsNotSilentlyAcceptedAsRigidAlignment()
    {
        Assert.False(FiducialAlignment.TryCreate([new(0, 0), new(100, 0)], [new(0, 0), new(120, 0)], out _, out var error));
        Assert.Contains("오차", error);
    }

    [Fact]
    public void AmbiguousRotationAndOverflowAreRejected()
    {
        Assert.False(FiducialAlignment.TryCreate([new(-10, -10), new(10, -10), new(10, 10), new(-10, 10)],
            [new(10, -10), new(-10, -10), new(-10, 10), new(10, 10)], out _, out _));
        Assert.False(FiducialAlignment.TryCreate([new(double.MaxValue, 0)], [new(-double.MaxValue, 0)], out _, out _));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void FullRotationSweepPreservesIndependentCheckPoint(int count)
    {
        PointD[] reference = [new(100, 300), new(17000, 300), new(17000, 30000), new(100, 30000)];
        for (var degrees = -180; degrees <= 180; degrees += 10)
        {
            var radians = degrees * Math.PI / 180;
            PointD Expected(PointD p) => new((Math.Cos(radians) * p.X) - (Math.Sin(radians) * p.Y) + 150,
                (Math.Sin(radians) * p.X) + (Math.Cos(radians) * p.Y) - 75);
            var detected = reference.Select(Expected).ToArray();
            Assert.True(FiducialAlignment.TryCreate(reference.AsSpan(0, count), detected.AsSpan(0, count), out var alignment, out var error), error);
            AssertClose(Expected(new(8765, 23456)), alignment.Transform.Apply(new(8765, 23456)));
        }
    }

    [Theory]
    [InlineData(RoiShapeKind.Rectangle)]
    [InlineData(RoiShapeKind.Circle)]
    [InlineData(RoiShapeKind.Triangle)]
    [InlineData(RoiShapeKind.Polygon)]
    public void EveryRoiShapeKeepsItsLocalGeometry(RoiShapeKind kind)
    {
        var roi = RoiGeometry.Create(kind, new(100, 100), 20, 20);
        Assert.True(FiducialAlignment.TryCreate([new(0, 0), new(100, 0)], [new(20, 30), new(20, 130)], out var alignment, out _));
        var mapped = alignment.Apply(roi);
        Assert.Same(roi.Shape, mapped.Shape);
        Assert.Equal(roi.Width, mapped.Width);
        Assert.Equal(roi.Height, mapped.Height);
        AssertClose(alignment.Transform.Apply(roi.ToWorld(new(5, 7))), mapped.ToWorld(new(5, 7)));
    }

    private static void AssertClose(PointD expected, PointD actual)
    {
        Assert.InRange((expected - actual).Length, 0, 1e-8);
    }
}
