using System.Collections.Immutable;
using VisionAlgorithm.OpenSrc.PatternMatching;

namespace VisionAlgorithm.Tests;

public sealed class LocalOrientationScoresTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(11, 8)]
    [InlineData(28, 19)]
    public void VectorScoresEqualScalarAtClippedEdges(int left, int top)
    {
        const int Width = 37, Height = 29;
        var bits = new byte[Width * Height];
        var random = new Random(172);
        random.NextBytes(bits);
        var map = OrientationMaps.Create(bits, Width, Height, 2, 4);
        var features = Enumerable.Range(0, 512).Select(_ => new QuantizedFeature(random.Next(-40, 40),
            random.Next(-30, 30), random.Next(8))).ToImmutableArray();
        Span<ushort> actual = stackalloc ushort[81];
        map.AccumulateLocal(features, left, top, 9, 9, actual);
        for (var y = 0; y < 9; y++)
        {
            for (var x = 0; x < 9; x++)
            {
                var expected = features.Sum(feature => map.At(feature.Bin, left + x + feature.X, top + y + feature.Y));
                Assert.Equal(expected, actual[(y * 9) + x]);
            }
        }
    }
}
