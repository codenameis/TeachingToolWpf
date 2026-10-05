using TeachingToolWpf.Imaging.Matching;
using VisionAlgorithm;

namespace TeachingToolWpf.Imaging.Tests;

public sealed class InspectionTileReducerTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    public void TileReductionMatchesIndependentScalarAcrossOffsetsStridesAndPartialEdges(int factor)
    {
        int[] dimensions = [1, 2, 3, 4, 5, 31, 32, 33, 127, 128, 129, 511, 512, 513];
        var random = new Random(6704);
        foreach (var width in dimensions)
        {
            foreach (var height in dimensions)
            {
                const int Offset = 3;
                var stride = width + 7;
                var input = new byte[(stride * height) + Offset + 9];
                random.NextBytes(input);
                var original = new GrayImage(input.AsMemory(Offset), width, height, stride);
                var expected = ScalarReduce(original, factor);
                using var actual = InspectionTileReducer.Reduce(original, factor, default);
                Assert.Equal(expected.Width, actual.Width);
                Assert.Equal(expected.Height, actual.Height);
                Assert.Equal(expected.Pixels.ToArray(), actual.Span.ToArray());
            }
        }
    }

    [Fact]
    public void FactorFourKeepsIntermediateRoundingInsteadOfOneSixteenPixelAverage()
    {
        var input = new byte[32 * 4];
        for (var x = 0; x < 32; x += 4)
        {
            input[x] = 2;
            input[x + 2] = 2;
        }
        using var actual = InspectionTileReducer.Reduce(new(input, 32, 4, 32), 4, default);
        Assert.Equal(Enumerable.Repeat((byte)1, 8), actual.Span.ToArray());
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void MaximumIntensityDoesNotOverflowOrSaturateEarly(int factor)
    {
        var input = new byte[512 * 512];
        input.AsSpan().Fill(255);
        using var actual = InspectionTileReducer.Reduce(new(input, 512, 512, 512), factor, default);
        Assert.True(actual.Span.IndexOfAnyExcept((byte)255) < 0);
    }

    [Fact]
    public void CancelledReductionDoesNotPublishAResult()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => InspectionTileReducer.Reduce(new(new byte[64], 8, 8, 8), 4, cancellation.Token));
    }

    private static GrayImage ScalarReduce(GrayImage image, int factor)
    {
        while (factor > 1)
        {
            var width = Math.Max(1, image.Width / 2);
            var height = Math.Max(1, image.Height / 2);
            var output = new byte[width * height];
            var source = image.Pixels.Span;
            for (var y = 0; y < height; y++)
            {
                var top = 2 * y * image.Stride;
                var bottom = Math.Min((2 * y) + 1, image.Height - 1) * image.Stride;
                for (var x = 0; x < width; x++)
                {
                    var left = 2 * x;
                    var right = Math.Min(left + 1, image.Width - 1);
                    output[(y * width) + x] = (byte)((source[top + left] + source[top + right] +
                        source[bottom + left] + source[bottom + right] + 2) / 4);
                }
            }
            image = new(output, width, height, width);
            factor /= 2;
        }
        if (image.Stride == image.Width && image.Pixels.Length == image.Width * image.Height)
        {
            return image;
        }
        var packed = new byte[image.Width * image.Height];
        for (var y = 0; y < image.Height; y++)
        {
            image.Pixels.Span.Slice(y * image.Stride, image.Width).CopyTo(packed.AsSpan(y * image.Width));
        }
        return new(packed, image.Width, image.Height, image.Width);
    }
}
