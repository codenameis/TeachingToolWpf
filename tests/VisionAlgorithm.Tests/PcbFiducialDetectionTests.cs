using VisionAlgorithm.OpenSrc.ShapeDetection;

namespace VisionAlgorithm.Tests;

public sealed class PcbFiducialDetectionTests
{
    [Theory]
    [InlineData(PrimitiveShapeKind.Diamond)]
    [InlineData(PrimitiveShapeKind.CrossHash)]
    [InlineData(PrimitiveShapeKind.Star)]
    [InlineData(PrimitiveShapeKind.Asterisk)]
    [InlineData(PrimitiveShapeKind.DoubleSquare)]
    public void MarksSurviveRotationScalePolarityAndNoise(PrimitiveShapeKind kind)
    {
        var failures = new List<string>();
        foreach (var scale in new[] { .6, 1, 1.5 })
        {
            for (var angle = 0; angle < 180; angle++)
            {
                var result = PrimitiveShapeDetector.Detect(PcbMarkFixture.Create(kind, angle, scale, angle % 2 == 0), new());
                if (result.Shapes.Length != 1 || result.Shapes[0].Kind != kind)
                {
                    failures.Add($"{kind}@{angle}/{scale}: {string.Join(',', result.Shapes.Select(shape => shape.Kind))}");
                    continue;
                }
                var shape = result.Shapes[0];
                Assert.InRange(Math.Abs(shape.Center.X - 120), 0, 1);
                Assert.InRange(Math.Abs(shape.Center.Y - 120), 0, 1);
                Assert.InRange(shape.Score, .8, 1);
                var period = kind switch
                {
                    PrimitiveShapeKind.Diamond or PrimitiveShapeKind.DoubleSquare => 180,
                    PrimitiveShapeKind.Star => 72,
                    PrimitiveShapeKind.Asterisk => 60,
                    _ => 90
                };
                Assert.InRange(Math.Abs(Math.IEEERemainder((shape.AngleRadians * 180 / Math.PI) - angle, period)), 0, 3);
                Assert.Empty(result.ClippedCenters);
                Assert.False(result.LimitExceeded);
                Assert.Equal(kind == PrimitiveShapeKind.CrossHash ? 1 : 0, shape.InnerContours.Length);
                Assert.Equal(kind == PrimitiveShapeKind.DoubleSquare ? 1 : 0, shape.AdditionalContours.Length);
            }
        }
        Assert.True(failures.Count == 0, string.Join("; ", failures));
    }

    [Fact]
    public void EightRayAsteriskIsDistinctFromFivePointStarAndCross()
    {
        foreach (var scale in new[] { .6, 1, 1.5 })
        {
            for (var angle = 0; angle < 45; angle++)
            {
                var result = PrimitiveShapeDetector.Detect(PcbMarkFixture.Create(PrimitiveShapeKind.Asterisk, angle, scale, rays: 8), new());
                Assert.True(result.Shapes.Length == 1 && result.Shapes[0].Kind == PrimitiveShapeKind.Asterisk, $"8 rays at {angle}/{scale}: {result.Shapes.Length}");
                Assert.InRange(Math.Abs(Math.IEEERemainder((result.Shapes[0].AngleRadians * 180 / Math.PI) - angle, 45)), 0, 3);
            }
        }
    }

    [Theory]
    [InlineData(PrimitiveShapeKind.Diamond)]
    [InlineData(PrimitiveShapeKind.CrossHash)]
    [InlineData(PrimitiveShapeKind.Star)]
    [InlineData(PrimitiveShapeKind.Asterisk)]
    [InlineData(PrimitiveShapeKind.DoubleSquare)]
    public void ShapeSelectionAndDamagedMarkRejectionAreIndependent(PrimitiveShapeKind kind)
    {
        var options = new PrimitiveDetectionOptions { Shapes = kind };
        Assert.Equal(kind, Assert.Single(PrimitiveShapeDetector.Detect(PcbMarkFixture.Create(kind), options).Shapes).Kind);
        Assert.DoesNotContain(PrimitiveShapeDetector.Detect(PcbMarkFixture.Create(kind), options with
        {
            Shapes = PrimitiveShapeKind.All & ~kind
        }).Shapes, shape => shape.Kind == kind);
        foreach (var angle in new[] { 0, 17, 39 })
        {
            var damaged = PcbMarkFixture.Create(kind, angle, contains: (x, y) =>
                PcbMarkFixture.Contains(kind, x, y) && !(x > 25 && y < 12 && y > -28));
            Assert.Empty(PrimitiveShapeDetector.Detect(damaged, options).Shapes);
        }
    }

    [Fact]
    public void RotatedSquareHasOneResultWithRectanglePrecedenceAndDiamondAlias()
    {
        var image = PcbMarkFixture.Create(PrimitiveShapeKind.Diamond, 45, contains: (x, y) => Math.Abs(x) <= 35 && Math.Abs(y) <= 35);
        Assert.Equal(PrimitiveShapeKind.Rectangle, Assert.Single(PrimitiveShapeDetector.Detect(image, new()).Shapes).Kind);
        Assert.Equal(PrimitiveShapeKind.Diamond, Assert.Single(PrimitiveShapeDetector.Detect(image,
            new()
            {
                Shapes = PrimitiveShapeKind.Diamond
            }).Shapes).Kind);
    }

    [Fact]
    public void DoubleSquareUsesCompoundSizeAndRejectsAmbiguousThreeSquareChain()
    {
        var image = PcbMarkFixture.Create(PrimitiveShapeKind.DoubleSquare);
        var options = new PrimitiveDetectionOptions { Shapes = PrimitiveShapeKind.DoubleSquare, MinimumSize = 60, MaximumSize = 80 };
        var shape = Assert.Single(PrimitiveShapeDetector.Detect(image, options).Shapes);
        Assert.Single(shape.AdditionalContours);
        Assert.InRange(shape.AreaPixels, 2000, 2400);
        Assert.Empty(PrimitiveShapeDetector.Detect(image, options with
        {
            MaximumSize = 65
        }).Shapes);
        Assert.Empty(PrimitiveShapeDetector.Detect(image, options with
        {
            MinimumSize = 75
        }).Shapes);
        var ambiguous = PcbMarkFixture.Create(PrimitiveShapeKind.DoubleSquare, contains: (x, y) =>
            new[] { -38, 0, 38 }.Any(center => Math.Abs(x - center) <= 17 && Math.Abs(y + center) <= 17));
        var result = PrimitiveShapeDetector.Detect(ambiguous, new());
        Assert.Equal(3, result.Shapes.Length);
        Assert.All(result.Shapes, candidate => Assert.Equal(PrimitiveShapeKind.Rectangle, candidate.Kind));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void DoubleSquareWithNarrowDiagonalGapKeepsBothSquares(double gap)
    {
        for (var angle = 0; angle < 180; angle += 3)
        {
            var offset = 17 + (gap / 2);
            var image = PcbMarkFixture.Create(PrimitiveShapeKind.DoubleSquare, angle, contains: (x, y) =>
                (Math.Abs(x - offset) <= 17 && Math.Abs(y + offset) <= 17) ||
                (Math.Abs(x + offset) <= 17 && Math.Abs(y - offset) <= 17));
            var result = PrimitiveShapeDetector.Detect(image, new()
            {
                Shapes = PrimitiveShapeKind.DoubleSquare
            });
            Assert.True(result.Shapes.Length == 1, $"gap={gap} angle={angle}: {result.Shapes.Length}");
            Assert.InRange(Math.Abs(Math.IEEERemainder((result.Shapes[0].AngleRadians * 180 / Math.PI) - angle, 180)), 0, 1.5);
        }
    }

    [Fact]
    public void MaskInsideEitherComponentRejectsDoubleSquareButEmptyGapDoesNot()
    {
        var image = PcbMarkFixture.Create(PrimitiveShapeKind.DoubleSquare);
        foreach (var x in new[] { 101, 120, 139 })
        {
            var mask = new byte[image.Width * image.Height];
            mask[((240 - x) * image.Width) + x] = 255;
            var masked = new ShapeGrayImage(image.Pixels, image.Width, image.Height, image.Stride, mask);
            var result = PrimitiveShapeDetector.Detect(masked, new()
            {
                Shapes = PrimitiveShapeKind.DoubleSquare
            });
            Assert.Equal(x == 120 ? 1 : 0, result.Shapes.Length);
        }
    }
}
