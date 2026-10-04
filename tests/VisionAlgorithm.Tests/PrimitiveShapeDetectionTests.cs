using VisionAlgorithm.OpenSrc.PatternMatching;
using VisionAlgorithm.OpenSrc.ShapeDetection;

namespace VisionAlgorithm.Tests;

public sealed class PrimitiveShapeDetectionTests
{
    [Theory]
    [InlineData(PrimitiveShapeKind.Triangle, 0, 1)]
    [InlineData(PrimitiveShapeKind.Triangle, 31, .8)]
    [InlineData(PrimitiveShapeKind.Rectangle, 0, 1)]
    [InlineData(PrimitiveShapeKind.Rectangle, 27, 1.3)]
    [InlineData(PrimitiveShapeKind.Circle, 0, .7)]
    [InlineData(PrimitiveShapeKind.Circle, 41, 1.2)]
    [InlineData(PrimitiveShapeKind.Cross, 0, 1)]
    [InlineData(PrimitiveShapeKind.Cross, 29, 1.25)]
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
    [InlineData("ring")]
    [InlineData("i")]
    public void UnsupportedSymbolsAreNotRelabeledAsBasicShapes(string symbol)
    {
        var image = Scene((x, y) => symbol switch
        {
            "ellipse" => (x * x / 2025) + (y * y / 625) <= 1,
            "rhombus" => (Math.Abs(x) / 45) + (Math.Abs(y) / 25) <= 1,
            "t" => (Math.Abs(x) <= 40 && y >= -40 && y <= -15) || (Math.Abs(x) <= 12 && y >= -40 && y <= 40),
            "l" => (x >= -40 && x <= -15 && Math.Abs(y) <= 40) || (Math.Abs(x) <= 40 && y >= 15 && y <= 40),
            "ring" => (x * x) + (y * y) is >= 400 and <= 1600,
            "i" => (Math.Abs(x) <= 48 && Math.Abs(y) is >= 16 and <= 48) || (Math.Abs(x) <= 16 && Math.Abs(y) <= 48),
            _ => false
        }, 19, 1, false, false);
        Assert.Empty(PrimitiveShapeDetector.Detect(image, new()).Shapes);
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
