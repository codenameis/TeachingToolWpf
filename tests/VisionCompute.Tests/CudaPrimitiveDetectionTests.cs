using VisionAlgorithm;
using VisionAlgorithm.OpenSrc.ShapeDetection;
using VisionCompute.ILGPU;

namespace VisionCompute.Tests;

public sealed class CudaPrimitiveDetectionTests
{
    [Fact]
    public void ReusedTransferBuffersKeepConcurrentOutputsAndDestinationSlicesIndependent()
    {
        using var gpu = new CudaPrimitivePreprocessor();
        var retained = new System.Collections.Concurrent.ConcurrentBag<(byte[] Expected, Memory<byte> Actual)>();
        Parallel.ForEach(new[] { 17, 63, 8, 127, 1, 31, 7 }, width =>
        {
            var height = width + 2;
            var stride = width + 5;
            var input = new byte[stride * height];
            new Random(width).NextBytes(input);
            var expected = new byte[width * height];
            Span<byte> neighborhood = stackalloc byte[9];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var index = 0;
                    for (var dy = -1; dy <= 1; dy++)
                    {
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            neighborhood[index++] = input[(Math.Clamp(y + dy, 0, height - 1) * stride) + Math.Clamp(x + dx, 0, width - 1)];
                        }
                    }
                    neighborhood.Sort();
                    expected[(y * width) + x] = neighborhood[4];
                }
            }
            var storage = Enumerable.Repeat((byte)197, expected.Length + 14).ToArray();
            var destination = storage.AsMemory(7, expected.Length);
            gpu.Median3x3(new(input, width, height, stride), default, destination);
            Assert.All(storage.Take(7).Concat(storage.TakeLast(7)), value => Assert.Equal((byte)197, value));
            retained.Add((expected, destination));
        });
        // 뒤의 큰/작은 창 처리와 장치 버퍼 재사용이 앞의 반환 영상을 덮어쓰면 안 된다.
        foreach (var (expected, actual) in retained)
        {
            Assert.Equal(expected, actual.ToArray());
        }
        Assert.Equal(7, gpu.Diagnostics.Calls);
    }

    [Fact]
    public void PcbMarksHaveIdenticalCpuAndCudaComponentsAndMeasurements()
    {
        using var gpu = new CudaPrimitivePreprocessor();
        foreach (var kind in new[] { PrimitiveShapeKind.Diamond, PrimitiveShapeKind.CrossHash, PrimitiveShapeKind.DoubleSquare,
            PrimitiveShapeKind.Star, PrimitiveShapeKind.Asterisk })
        {
            foreach (var angle in new[] { 3, 27, 42 })
            {
                foreach (var dark in new[] { false, true })
                {
                    var image = VisionAlgorithm.Tests.PcbMarkFixture.Create(kind, angle, .6, dark);
                    var expected = PrimitiveShapeDetector.Detect(image, new());
                    var actual = PrimitiveShapeDetector.Detect(image, new()
                    {
                        UseGpu = true
                    }, default, gpu);
                    Assert.Single(expected.Shapes);
                    Assert.Equal(System.Text.Json.JsonSerializer.Serialize(expected), System.Text.Json.JsonSerializer.Serialize(actual));
                }
            }
        }

        Assert.Contains("NVIDIA", gpu.Diagnostics.Device, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TouchingDoubleSquareAlsoKeepsCpuCudaIdentity()
    {
        using var gpu = new CudaPrimitivePreprocessor();
        var image = VisionAlgorithm.Tests.PcbMarkFixture.Create(PrimitiveShapeKind.DoubleSquare, 27, contains: (x, y) =>
            (Math.Abs(x - 17) <= 17 && Math.Abs(y + 17) <= 17) || (Math.Abs(x + 17) <= 17 && Math.Abs(y - 17) <= 17));
        var cpu = PrimitiveShapeDetector.Detect(image, new());
        Assert.Equal(PrimitiveShapeKind.DoubleSquare, Assert.Single(cpu.Shapes).Kind);
        var actual = PrimitiveShapeDetector.Detect(image, new()
        {
            UseGpu = true
        }, default, gpu);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(cpu), System.Text.Json.JsonSerializer.Serialize(actual));
    }

    [Theory]
    [InlineData(1, 1, 0)]
    [InlineData(1, 19, 3)]
    [InlineData(27, 1, 5)]
    [InlineData(2, 3, 1)]
    [InlineData(127, 93, 7)]
    [InlineData(1300, 1200, 11)]
    public void MedianMatchesIndependentSortedNeighborhoodIncludingStrideAndBorders(int width, int height, int padding)
    {
        var stride = width + padding;
        var pixels = new byte[stride * height];
        new Random(203).NextBytes(pixels);
        var original = pixels.ToArray();
        var image = new ShapeGrayImage(pixels, width, height, stride);
        using var gpu = new CudaPrimitivePreprocessor();
        var actual = gpu.Median3x3(image, default);
        Span<byte> neighborhood = stackalloc byte[9];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = 0;
                for (var dy = -1; dy <= 1; dy++)
                {
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        neighborhood[offset++] = pixels[(Math.Clamp(y + dy, 0, height - 1) * stride) + Math.Clamp(x + dx, 0, width - 1)];
                    }
                }
                neighborhood.Sort();
                Assert.Equal(neighborhood[4], actual.Pixels.Span[(y * width) + x]);
            }
        }
        Assert.Equal(original, pixels);
        Assert.Equal(1, gpu.Diagnostics.Calls);
        Assert.Equal((long)width * height, gpu.Diagnostics.Pixels);
        Assert.Contains("NVIDIA", gpu.Diagnostics.Device, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void CpuAndGpuHaveIdenticalContoursScoresAndMaskSemantics(bool automatic, bool masked)
    {
        const int Width = 700;
        const int Height = 400;
        var pixels = new byte[Width * Height];
        var mask = masked ? new byte[pixels.Length] : [];
        var random = new Random(926);
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var rectangle = x is > 60 and < 190 && y is > 80 and < 170 &&
                    !(x is > 80 and < 170 && y is > 100 and < 150);
                var radiusSquared = ((x - 320) * (x - 320)) + ((y - 260) * (y - 260));
                var circle = radiusSquared is < 225 or (> 625 and < 1600);
                var cross = (Math.Abs(x - 550) < 15 && Math.Abs(y - 150) < 50) ||
                    (Math.Abs(x - 550) < 50 && Math.Abs(y - 150) < 15);
                pixels[(y * Width) + x] = (byte)((rectangle || circle || cross ? 220 : 25) + random.Next(-10, 11));
                if (masked && x is > 100 and < 110 && y is > 120 and < 130)
                {
                    mask[(y * Width) + x] = 255;
                }
            }
        }
        var image = new ShapeGrayImage(pixels, Width, Height, Width, mask);
        using var gpu = new CudaPrimitivePreprocessor();
        foreach (var polarity in Enum.GetValues<ForegroundPolarity>())
        {
            var options = new PrimitiveDetectionOptions { AutomaticThreshold = automatic, Polarity = polarity };
            var cpu = PrimitiveShapeDetector.Detect(image, options);
            var actual = PrimitiveShapeDetector.Detect(image, options with
            {
                UseGpu = true
            }, default, gpu);
            if (polarity != ForegroundPolarity.Dark)
            {
                Assert.Equal(4, cpu.Shapes.Length);
                Assert.Contains(cpu.Shapes, shape => shape.Kind == PrimitiveShapeKind.Ring);
                Assert.Contains(cpu.Shapes, shape => shape.Kind == PrimitiveShapeKind.RectangleFrame);
                Assert.Contains(cpu.Shapes, shape => shape.Kind == PrimitiveShapeKind.Circle);
            }
            Assert.Equal(cpu.Shapes.Length, actual.Shapes.Length);
            Assert.Equal(cpu.ClippedCenters.ToArray(), actual.ClippedCenters.ToArray());
            Assert.Equal(cpu.LimitExceeded, actual.LimitExceeded);
            for (var index = 0; index < cpu.Shapes.Length; index++)
            {
                var expected = cpu.Shapes[index];
                var shape = actual.Shapes[index];
                Assert.Equal(expected with
                {
                    Contour = shape.Contour,
                    InnerContours = shape.InnerContours
                }, shape);
                Assert.Equal(expected.Contour.ToArray(), shape.Contour.ToArray());
                Assert.Equal(expected.InnerContours.Length, shape.InnerContours.Length);
                for (var hole = 0; hole < expected.InnerContours.Length; hole++)
                {
                    Assert.Equal(expected.InnerContours[hole].ToArray(), shape.InnerContours[hole].ToArray());
                }
            }
        }
    }

    [Fact]
    public void CancellationDisposalAndWindowBudgetDoNotReturnPartialSuccess()
    {
        var gpu = new CudaPrimitivePreprocessor();
        var image = new ShapeGrayImage(new byte[64], 8, 8, 8);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => gpu.Median3x3(image, cancellation.Token));
        Assert.Equal(0, gpu.Diagnostics.Calls);
        Assert.Throws<ArgumentException>(() => gpu.Median3x3(new(new byte[1500 * 1500], 1500, 1500, 1500), default));
        Assert.Throws<ArgumentException>(() => gpu.Median3x3(image, default, new byte[63]));
        var destination = new byte[65];
        destination[64] = 255;
        var prepared = gpu.Median3x3(image, default, destination);
        Assert.Equal(64, prepared.Pixels.Length);
        Assert.Equal(255, destination[64]);
        Assert.NotNull(gpu.Median3x3(image, default));
        gpu.Dispose();
        gpu.Dispose();
        Assert.Throws<ObjectDisposedException>(() => gpu.Median3x3(image, default));
    }
}
