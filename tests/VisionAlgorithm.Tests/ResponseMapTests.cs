using VisionAlgorithm;
using VisionAlgorithm.OpenSrc.PatternMatching;

namespace VisionAlgorithm.Tests;

public sealed class ResponseMapTests
{
    [Fact]
    public void GrayInputValidatesStrideLengthAndOverflow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GrayImage(new byte[4], 3, 2, 2));
        Assert.Throws<ArgumentException>(() => new GrayImage(new byte[4], 3, 2, 3));
        Assert.Throws<OverflowException>(() => new GrayImage(new byte[1], 2, int.MaxValue, int.MaxValue));
    }

    [Fact]
    public void OrientationHasUnsignedHalfTurnPeriod()
    {
        Assert.Equal(0, OrientationMaps.Quantize(1, 0));
        Assert.Equal(0, OrientationMaps.Quantize(-1, 0));
        Assert.Equal(4, OrientationMaps.Quantize(0, 1));
        Assert.Equal(2, OrientationMaps.Quantize(1, 1));
    }

    [Fact]
    public void SpreadAndResponseMatchIndependentNeighborhoodFormula()
    {
        const int Width = 11;
        const int Height = 9;
        var bits = new byte[Width * Height];
        bits[(4 * Width) + 5] = 1;
        bits[(3 * Width) + 6] = 16;
        var map = OrientationMaps.Create(bits, Width, Height, 2, 3);
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                for (var bin = 0; bin < 8; bin++)
                {
                    var expected = 0;
                    for (var dy = -2; dy <= 2; dy++)
                    {
                        for (var dx = -2; dx <= 2; dx++)
                        {
                            var px = x + dx;
                            var py = y + dy;
                            if (px < 0 || py < 0 || px >= Width || py >= Height)
                            {
                                continue;
                            }
                            for (var other = 0; other < 8; other++)
                            {
                                if ((bits[(py * Width) + px] & (1 << other)) != 0)
                                {
                                    expected = Math.Max(expected, (int)Math.Round(100 * Math.Abs(Math.Cos((bin - other) * Math.PI / 8))));
                                }
                            }
                        }
                    }
                    Assert.Equal(expected, map.At(bin, x, y));
                }
            }
        }
    }
}
