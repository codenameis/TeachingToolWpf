using VisionAlgorithm.OpenSrc.PatternMatching;
using VisionAlgorithm.OpenSrc.ShapeDetection;

namespace VisionAlgorithm.Tests;

public sealed class PrimitiveShapeDetectionTests
{
    [Fact]
    public void SmallNestedRectangleSurvivesEveryAngleIncludingBeveledCorners()
    {
        for (var angle = 0; angle < 90; angle++)
        {
            var image = Scene((x, y) => Math.Abs(x) <= 48 && Math.Abs(y) <= 36 &&
                (Math.Abs(x) >= 30 || Math.Abs(y) >= 20 || (Math.Abs(x) <= 16 && Math.Abs(y) <= 11)), angle, 1, false, false);
            var shapes = PrimitiveShapeDetector.Detect(image, new()).Shapes;
            Assert.Equal(2, shapes.Length);
            Assert.Single(shapes, shape => shape.Kind == PrimitiveShapeKind.RectangleFrame);
            var inner = Assert.Single(shapes, shape => shape.Kind == PrimitiveShapeKind.Rectangle);
            Assert.InRange(Math.Abs(inner.Center.X - 120), 0, .1);
            Assert.InRange(Math.Abs(inner.Center.Y - 120), 0, .1);
            // 작은 내부 도형의 정밀 각도 합격은 이 개수/위치 회귀와 별도다.
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NestedRingsAndIndependentDiskAreCountedOnceWithoutCountingEmptyHoles(bool dark)
    {
        var image = Scene((x, y) => (x * x) + (y * y) is <= 64 or (>= 225 and <= 625) or (>= 1024 and <= 2500),
            0, 1, dark, false);
        var result = PrimitiveShapeDetector.Detect(image, new());
        Assert.Equal(3, result.Shapes.Length);
        Assert.Equal(2, result.Shapes.Count(shape => shape.Kind == PrimitiveShapeKind.Ring));
        Assert.Single(result.Shapes, shape => shape.Kind == PrimitiveShapeKind.Circle);
        Assert.All(result.Shapes, shape =>
        {
            Assert.InRange(Math.Abs(shape.Center.X - 120), 0, .1);
            Assert.InRange(Math.Abs(shape.Center.Y - 120), 0, .1);
            Assert.Equal(shape.Kind == PrimitiveShapeKind.Ring ? 1 : 0, shape.InnerContours.Length);
        });
        var rings = PrimitiveShapeDetector.Detect(image, new()
        {
            Shapes = PrimitiveShapeKind.Ring
        });
        Assert.Equal(2, rings.Shapes.Length);
        Assert.False(result.LimitExceeded);
        Assert.Empty(result.ClippedCenters);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(27)]
    [InlineData(89)]
    public void RectangleFrameRetainsItsHoleAndSeparatelyDetectsInnerRectangle(int degrees)
    {
        var image = Scene((x, y) => Contains(PrimitiveShapeKind.RectangleFrame, x, y) ||
            (Math.Abs(x) <= 12 && Math.Abs(y) <= 8), degrees, 1, false, false);
        var shapes = PrimitiveShapeDetector.Detect(image, new()).Shapes;
        Assert.Equal(2, shapes.Length);
        var frame = Assert.Single(shapes, shape => shape.Kind == PrimitiveShapeKind.RectangleFrame);
        Assert.Single(shapes, shape => shape.Kind == PrimitiveShapeKind.Rectangle);
        var hole = Assert.Single(frame.InnerContours);
        Assert.InRange(frame.AreaPixels, 2000, 3200);
        Assert.All(hole, point => Assert.InRange(Math.Sqrt(Math.Pow(point.X - 120, 2) + Math.Pow(point.Y - 120, 2)), 10, 32));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(19, true)]
    [InlineData(30, true)]
    public void HoleOnlyMaskKeepsRingButMaskOnMaterialOrInnerEdgeRejectsIt(int offset, bool rejected)
    {
        var image = Scene((x, y) => Contains(PrimitiveShapeKind.Ring, x, y), 0, 1, false, false);
        var mask = new byte[image.Width * image.Height];
        mask[(120 * image.Width) + 120 + offset] = 255;
        var masked = new GrayImage(image.Pixels, image.Width, image.Height, image.Stride, mask);
        var shapes = PrimitiveShapeDetector.Detect(masked, new()).Shapes;
        if (rejected)
        {
            Assert.Empty(shapes);
        }
        else
        {
            Assert.Equal(PrimitiveShapeKind.Ring, Assert.Single(shapes).Kind);
        }
    }

    [Fact]
    public void AllSupportedShapesAreDetectedThroughoutQuarterTurn()
    {
        var failures = new List<string>();
        foreach (var kind in new[] { PrimitiveShapeKind.Triangle, PrimitiveShapeKind.Rectangle, PrimitiveShapeKind.Circle,
            PrimitiveShapeKind.Cross, PrimitiveShapeKind.Ring, PrimitiveShapeKind.RectangleFrame })
        {
            for (var angle = 0; angle < 90; angle++)
            {
                var image = Scene((x, y) => Contains(kind, x, y), angle, 1, false, false);
                var result = PrimitiveShapeDetector.Detect(image, new());
                if (result.Shapes.Length != 1 || result.Shapes[0].Kind != kind)
                {
                    failures.Add($"{kind}@{angle}: {result.Shapes.Length}");
                }
            }
        }
        Assert.True(failures.Count == 0, string.Join(", ", failures));
    }

    [Fact]
    public void SmallSquareDoesNotBecomeACircleWhenContourCompressionRemovesItsStraightSides()
    {
        var image = Scene((x, y) => Math.Abs(x) <= 4.5 && Math.Abs(y) <= 4.5, 0, 1, false, false);
        Assert.DoesNotContain(PrimitiveShapeDetector.Detect(image, new()
        {
            MinimumSize = 6
        }).Shapes,
            shape => shape.Kind == PrimitiveShapeKind.Circle);
    }

    [Fact]
    public void RectangleAndCrossAnglesUseFittedSidesInsteadOfRoundedApproximationCorners()
    {
        foreach (var kind in new[] { PrimitiveShapeKind.Rectangle, PrimitiveShapeKind.Cross, PrimitiveShapeKind.RectangleFrame })
        {
            for (var angle = 0; angle < 90; angle++)
            {
                var image = Scene((x, y) => Contains(kind, x, y), angle, 1, false, false);
                var shape = Assert.Single(PrimitiveShapeDetector.Detect(image, new()).Shapes);
                var error = Math.Abs(Math.IEEERemainder((shape.AngleRadians * 180 / Math.PI) - angle, 90));
                Assert.True(error <= .75, $"{kind}@{angle}: {error:F4} degrees");
            }
        }
    }

    [Fact]
    public void DefaultSearchReturnsEveryShapeBeyondFormerResultAndContourCaps()
    {
        const int Side = 1432;
        var pixels = new byte[Side * Side];
        for (var row = 0; row < 65; row++)
        {
            for (var column = 0; column < 65; column++)
            {
                for (var y = 0; y < 20; y++)
                {
                    pixels.AsSpan(((2 + (row * 22) + y) * Side) + 2 + (column * 22), 20).Fill(220);
                }
            }
        }
        var options = new PrimitiveDetectionOptions { MinimumSize = 6, MaximumSize = 24, Polarity = ForegroundPolarity.Bright };
        Assert.Equal(0, options.MaximumResults);
        var result = PrimitiveShapeDetector.Detect(new(pixels, Side, Side, Side), options);
        Assert.Equal(4225, result.Shapes.Length);
        Assert.All(result.Shapes, shape => Assert.Equal(PrimitiveShapeKind.Rectangle, shape.Kind));
        Assert.False(result.LimitExceeded);
        Assert.Empty(result.ClippedCenters);
        var limited = PrimitiveShapeDetector.Detect(new(pixels, Side, Side, Side), options with
        {
            MaximumResults = 4000
        });
        Assert.Equal(4000, limited.Shapes.Length);
        Assert.True(limited.LimitExceeded);
        Assert.Equal(result.Shapes.Take(4000).Select(shape => (shape.Kind, shape.Center, shape.Score)),
            limited.Shapes.Select(shape => (shape.Kind, shape.Center, shape.Score)));
    }

    [Theory]
    [InlineData(PrimitiveShapeKind.Triangle, 0, 1)]
    [InlineData(PrimitiveShapeKind.Triangle, 31, .8)]
    [InlineData(PrimitiveShapeKind.Rectangle, 0, 1)]
    [InlineData(PrimitiveShapeKind.Rectangle, 27, 1.3)]
    [InlineData(PrimitiveShapeKind.Circle, 0, .7)]
    [InlineData(PrimitiveShapeKind.Circle, 41, 1.2)]
    [InlineData(PrimitiveShapeKind.Cross, 0, 1)]
    [InlineData(PrimitiveShapeKind.Cross, 29, 1.25)]
    [InlineData(PrimitiveShapeKind.Ring, 0, .7)]
    [InlineData(PrimitiveShapeKind.Ring, 41, 1.2)]
    [InlineData(PrimitiveShapeKind.RectangleFrame, 0, 1)]
    [InlineData(PrimitiveShapeKind.RectangleFrame, 27, 1.3)]
    public void FilledShapesSurviveRotationScaleNoiseAndStride(PrimitiveShapeKind kind, double angle, double scale)
    {
        var image = Scene((x, y) => Contains(kind, x, y), angle, scale, false, true);
        var output = PrimitiveShapeDetector.Detect(image, new());
        var shape = Assert.Single(output.Shapes);
        Assert.Equal(kind, shape.Kind);
        Assert.InRange(Math.Abs(shape.Center.X - 120), 0, 2);
        // 삼각형의 중심은 도형 외접 프레임 중심이 아닌 면적 중심이다.
        if (kind != PrimitiveShapeKind.Triangle)
        {
            Assert.InRange(Math.Abs(shape.Center.Y - 120), 0, 2);
        }
        Assert.InRange(shape.Score, .8, 1);
        Assert.True(shape.Contour.Length >= 3);
        Assert.All(shape.Contour, point =>
        {
            Assert.InRange(point.X, 0, image.Width);
            Assert.InRange(point.Y, 0, image.Height);
        });
        Assert.Empty(output.ClippedCenters);
        Assert.False(output.LimitExceeded);
    }

    [Theory]
    [InlineData("ellipse")]
    [InlineData("rhombus")]
    [InlineData("t")]
    [InlineData("l")]
    [InlineData("circle-with-square-hole")]
    [InlineData("two-holes")]
    [InlineData("i")]
    public void UnsupportedSymbolsAreNotRelabeledAsBasicShapes(string symbol)
    {
        for (var angle = 0; angle < 90; angle++)
        {
            var image = Scene((x, y) => symbol switch
        {
            "ellipse" => (x * x / 2025) + (y * y / 625) <= 1,
            "rhombus" => (Math.Abs(x) / 45) + (Math.Abs(y) / 25) <= 1,
            "t" => (Math.Abs(x) <= 40 && y >= -40 && y <= -15) || (Math.Abs(x) <= 12 && y >= -40 && y <= 40),
            "l" => (x >= -40 && x <= -15 && Math.Abs(y) <= 40) || (Math.Abs(x) <= 40 && y >= 15 && y <= 40),
            "circle-with-square-hole" => (x * x) + (y * y) <= 1600 && (Math.Abs(x) >= 15 || Math.Abs(y) >= 15),
            "two-holes" => Math.Abs(x) <= 45 && Math.Abs(y) <= 30 &&
                ((x - 20) * (x - 20)) + (y * y) > 100 && ((x + 20) * (x + 20)) + (y * y) > 100,
            "i" => (Math.Abs(x) <= 48 && Math.Abs(y) is >= 16 and <= 48) || (Math.Abs(x) <= 16 && Math.Abs(y) <= 48),
            _ => false
        }, angle, 1, false, false);
            Assert.Empty(PrimitiveShapeDetector.Detect(image, new()
            {
                Shapes = PrimitiveShapeKind.Triangle | PrimitiveShapeKind.Rectangle | PrimitiveShapeKind.Circle |
                    PrimitiveShapeKind.Cross | PrimitiveShapeKind.Ring | PrimitiveShapeKind.RectangleFrame
            }).Shapes);
            if (symbol != "rhombus")
            {
                Assert.Empty(PrimitiveShapeDetector.Detect(image, new()).Shapes);
            }
            else
            {
                Assert.Equal(PrimitiveShapeKind.Diamond, Assert.Single(PrimitiveShapeDetector.Detect(image, new()).Shapes).Kind);
            }
        }
    }

    [Theory]
    [InlineData(ForegroundPolarity.Auto)]
    [InlineData(ForegroundPolarity.Dark)]
    public void DarkForegroundAndManualThresholdUseRequestedPolarity(ForegroundPolarity polarity)
    {
        var image = Scene((x, y) => Contains(PrimitiveShapeKind.Rectangle, x, y), 0, 1, true, false);
        var result = PrimitiveShapeDetector.Detect(image, new()
        {
            AutomaticThreshold = false,
            Threshold = 128,
            Polarity = polarity
        });
        Assert.Equal(PrimitiveShapeKind.Rectangle, Assert.Single(result.Shapes).Kind);
    }

    [Fact]
    public void ShapeFilterSizeAndClippedImageBoundaryAreHonored()
    {
        var image = Scene((x, y) => Contains(PrimitiveShapeKind.Circle, x, y), 0, 1, false, false);
        Assert.Empty(PrimitiveShapeDetector.Detect(image, new()
        {
            Shapes = PrimitiveShapeKind.Triangle
        }).Shapes);
        Assert.Empty(PrimitiveShapeDetector.Detect(image, new()
        {
            MaximumSize = 40
        }).Shapes);
        var clipped = Scene((x, y) => ((x + 120) * (x + 120)) + (y * y) <= 1600, 0, 1, false, false);
        var result = PrimitiveShapeDetector.Detect(clipped, new());
        Assert.Empty(result.Shapes);
        Assert.NotEmpty(result.ClippedCenters);
    }

    [Fact]
    public void CancellationAndWindowMemoryBudgetAreCheckedBeforeNativeWork()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var image = Scene((x, y) => false, 0, 1, false, false);
        Assert.Throws<OperationCanceledException>(() => PrimitiveShapeDetector.Detect(image, new(), cancellation.Token));
        Assert.Throws<ArgumentException>(() => PrimitiveShapeDetector.Detect(new(new byte[1500 * 1500], 1500, 1500, 1500), new()));
        Assert.Throws<ArgumentOutOfRangeException>(() => PrimitiveShapeDetector.Detect(image, new() { MaximumSize = 513 }));
    }

    private static bool Contains(PrimitiveShapeKind kind, double x, double y) => kind switch
    {
        PrimitiveShapeKind.Ring => (x * x) + (y * y) is >= 400 and <= 1600,
        PrimitiveShapeKind.RectangleFrame => Math.Abs(x) <= 40 && Math.Abs(y) <= 27 && (Math.Abs(x) >= 25 || Math.Abs(y) >= 14),
        PrimitiveShapeKind.Rectangle => Math.Abs(x) <= 40 && Math.Abs(y) <= 27,
        PrimitiveShapeKind.Circle => (x * x) + (y * y) <= 1296,
        PrimitiveShapeKind.Cross => (Math.Abs(x) <= 12 && Math.Abs(y) <= 42) || (Math.Abs(x) <= 42 && Math.Abs(y) <= 12),
        PrimitiveShapeKind.Triangle => y >= -30 && y <= 60 && Math.Abs(x) <= (60 - y) / 2,
        _ => false
    };

    private static GrayImage Scene(Func<double, double, bool> inside, double degrees, double scale, bool dark, bool noise)
    {
        const int Size = 240;
        const int Stride = 247;
        var pixels = new byte[Size * Stride];
        var random = new Random(935);
        var cosine = Math.Cos(degrees * Math.PI / 180);
        var sine = Math.Sin(degrees * Math.PI / 180);
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var dx = (x + .5 - 120) / scale;
                var dy = (y + .5 - 120) / scale;
                var foreground = inside((cosine * dx) + (sine * dy), (-sine * dx) + (cosine * dy));
                var value = foreground != dark ? 220 : 30;
                pixels[(y * Stride) + x] = (byte)(value + (noise ? random.Next(-10, 11) : 0));
            }
        }
        return new(pixels, Size, Size, Stride);
    }
}
