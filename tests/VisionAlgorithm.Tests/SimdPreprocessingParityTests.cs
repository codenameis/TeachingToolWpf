using VisionAlgorithm.OpenSrc.PatternMatching;

namespace VisionAlgorithm.Tests;

public sealed class SimdPreprocessingParityTests
{
    [Theory]
    [InlineData(1, 1, 0)]
    [InlineData(2, 5, 3)]
    [InlineData(16, 1, 4)]
    [InlineData(17, 13, 7)]
    [InlineData(69, 73, 5)]
    [InlineData(513, 519, 17)]
    public void VectorizedReductionPreservesRoundedAreaMeanWithOddDimensionsAndStride(int width, int height, int padding)
    {
        var stride = width + padding;
        var pixels = new byte[stride * height];
        new Random(width + height).NextBytes(pixels);
        var result = GradientImage.Reduce(new(pixels, width, height, stride));
        var actual = result.Pixels.Span;

        for (var y = 0; y < result.Height; y++)
        {
            for (var x = 0; x < result.Width; x++)
            {
                var sum = 2;
                for (var dy = 0; dy < 2; dy++)
                {
                    for (var dx = 0; dx < 2; dx++)
                    {
                        sum += pixels[(Math.Min((2 * y) + dy, height - 1) * stride) + Math.Min((2 * x) + dx, width - 1)];
                    }
                }
                Assert.Equal((byte)(sum / 4), actual[(y * result.Stride) + x]);
            }
        }
    }

    [Theory]
    [InlineData(1, 1, 0)]
    [InlineData(2, 5, 3)]
    [InlineData(3, 2, 0)]
    [InlineData(67, 1, 3)]
    [InlineData(67, 3, 7)]
    [InlineData(67, 4, 1)]
    [InlineData(67, 5, 2)]
    [InlineData(67, 6, 3)]
    [InlineData(67, 8, 5)]
    [InlineData(31, 27, 7)]
    [InlineData(32, 33, 0)]
    [InlineData(35, 34, 5)]
    [InlineData(67, 73, 4)]
    [InlineData(269, 259, 11)]
    public void VectorizedPreparationMatchesIndependentIntegerConvolution(int width, int height, int padding)
    {
        var stride = width + padding;
        var pixels = new byte[stride * height];
        new Random((width * 65537) + height).NextBytes(pixels);
        var image = new GrayImage(pixels, width, height, stride);
        using var reference = new DirectConvolutionPreprocessor();

        var expected = GradientImage.Create(image, 8, default, reference);
        var actual = GradientImage.Create(image, 8, default);

        Assert.Equal(expected.X, actual.X);
        Assert.Equal(expected.Y, actual.Y);
        Assert.Equal(expected.Magnitude, actual.Magnitude);
        Assert.Equal(expected.Bits, actual.Bits);
        Assert.Equal(expected.EdgePoints, actual.EdgePoints);
    }

    [Theory]
    [InlineData(1, 1, 0, 1)]
    [InlineData(3, 2, 2, 4)]
    [InlineData(39, 37, 0, 3)]
    [InlineData(67, 73, 1, 4)]
    [InlineData(71, 69, 2, 3)]
    [InlineData(73, 67, 5, 4)]
    public void PackedResponseLookupPreservesAllNeighborhoodBitsAndPhaseRows(int width, int height, int radius, int step)
    {
        var bits = new byte[width * height];
        new Random(9197).NextBytes(bits);
        var map = OrientationMaps.Create(bits, width, height, radius, step);

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                byte neighborhood = 0;
                for (var dy = Math.Max(0, y - radius); dy <= Math.Min(height - 1, y + radius); dy++)
                {
                    for (var dx = Math.Max(0, x - radius); dx <= Math.Min(width - 1, x + radius); dx++)
                    {
                        neighborhood |= bits[(dy * width) + dx];
                    }
                }
                for (var bin = 0; bin < 8; bin++)
                {
                    var expected = 0d;
                    for (var orientation = 0; orientation < 8; orientation++)
                    {
                        if ((neighborhood & (1 << orientation)) != 0)
                        {
                            expected = Math.Max(expected, Math.Abs(Math.Cos((bin - orientation) * Math.PI / 8)));
                        }
                    }
                    Assert.Equal((byte)Math.Round(expected * 100), map.At(bin, x, y));
                    Assert.Equal(map.At(bin, x, y), map.Row(bin, x, y, 1)[0]);
                }
            }
        }
    }

    private sealed class DirectConvolutionPreprocessor : IImagePreprocessor
    {
        public ImagePreprocessingDiagnostics Diagnostics => new("Test", "Integer convolution", 0, 0, 0);

        public GradientPlanes Prepare(GrayImage image, float minimumGradient, CancellationToken cancellationToken = default)
        {
            var smooth = new float[image.Width * image.Height];
            int[] weights = [1, 4, 6, 4, 1];
            var input = image.Pixels.Span;
            for (var y = 0; y < image.Height; y++)
            {
                for (var x = 0; x < image.Width; x++)
                {
                    var sum = 0;
                    for (var dy = -2; dy <= 2; dy++)
                    {
                        for (var dx = -2; dx <= 2; dx++)
                        {
                            var sourceY = Math.Clamp(y + dy, 0, image.Height - 1);
                            var sourceX = Math.Clamp(x + dx, 0, image.Width - 1);
                            sum += weights[dy + 2] * weights[dx + 2] * input[(sourceY * image.Stride) + sourceX];
                        }
                    }
                    smooth[(y * image.Width) + x] = sum / 256f;
                }
            }
            var count = smooth.Length;
            var result = new GradientPlanes(new float[count], new float[count], new float[count], new byte[count]);
            for (var y = 1; y < image.Height - 1; y++)
            {
                for (var x = 1; x < image.Width - 1; x++)
                {
                    var i = (y * image.Width) + x;
                    var w = image.Width;
                    var dx = (smooth[i - w + 1] + (2 * smooth[i + 1]) + smooth[i + w + 1] -
                        smooth[i - w - 1] - (2 * smooth[i - 1]) - smooth[i + w - 1]) / 8;
                    var dy = (smooth[i + w - 1] + (2 * smooth[i + w]) + smooth[i + w + 1] -
                        smooth[i - w - 1] - (2 * smooth[i - w]) - smooth[i - w + 1]) / 8;
                    var magnitude = MathF.Sqrt((dx * dx) + (dy * dy));
                    result.Magnitude[i] = magnitude;
                    if (magnitude >= minimumGradient)
                    {
                        result.X[i] = dx / magnitude;
                        result.Y[i] = dy / magnitude;
                        var bin = (((int)Math.Round(Math.Atan2(dy, dx) * 8 / Math.PI) % 8) + 8) % 8;
                        result.Bits[i] = (byte)(1 << bin);
                    }
                }
            }
            return result;
        }

        public void Dispose()
        {
        }
    }
}
