using VisionImaging;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Imaging.Tests;

public sealed class OriginalGraySourceTests
{
    [Theory]
    [InlineData(2055, 2053)]
    [InlineData(8192, 8192)]
    public void DirectOriginalGrayEqualsEveryRgbaPixelIncludingNoiseAndSeams(int width, int height)
    {
        using var source = new ProceduralTileSource(width, height);
        int[] xs = [0, 1, 2, 3, (width - 1) / 512];
        int[] ys = [0, 1, 2, 3, (height - 1) / 512];
        foreach (var x in xs.Distinct())
        {
            foreach (var y in ys.Distinct())
            {
                using var color = source.Read(new(0, x, y), default);
                using var gray = source.ReadOriginalGray8(x, y, default);
                Assert.Equal(PixelKind.Gray8, gray.Kind);
                for (var row = 0; row < gray.Height; row++)
                {
                    for (var column = 0; column < gray.Width; column++)
                    {
                        var i = (row * color.Stride) + (column * 4);
                        var expected = ((77 * color.Span[i]) + (150 * color.Span[i + 1]) + (29 * color.Span[i + 2]) + 128) >> 8;
                        Assert.Equal(expected, gray.Span[(row * gray.Stride) + column]);
                    }
                }
            }
        }
    }

    [Fact]
    public void CanceledReadDoesNotPublishBuffer()
    {
        using var source = new ProceduralTileSource();
        Assert.ThrowsAny<OperationCanceledException>(() => source.ReadOriginalGray8(0, 0, new(true)));
    }
}
