using VisionAlgorithm.OpenSrc.PatternMatching;

namespace VisionAlgorithm.Tests;

public sealed class GpuCandidatePreparationTests
{
    [Theory]
    [InlineData(17, 13, 2, 4)]
    [InlineData(256, 1, 0, 1)]
    [InlineData(129, 127, 3, 3)]
    [InlineData(1, 19, 2, 4)]
    public void RasterOnlyMapsPreserveEveryLocalResponseAndRejectLinearAccess(int width, int height, int radius, int step)
    {
        var bits = new byte[width * height];
        for (var index = 0; index < bits.Length; index++)
        {
            bits[index] = (byte)index;
        }
        var full = OrientationMaps.Create(bits, width, height, radius, step);
        var compact = OrientationMaps.Create(bits, width, height, radius, step, createLinearResponses: false);

        Assert.True(full.HasLinearResponses);
        Assert.False(compact.HasLinearResponses);
        Assert.Equal(width, compact.Width);
        Assert.Equal(height, compact.Height);
        Assert.Equal(step, compact.Step);
        Assert.Equal(full.PlaneLength, compact.PlaneLength);
        Assert.Equal((long)width * height, compact.EstimatedBytes);
        Assert.Equal(8L * step * step * full.PlaneLength, full.EstimatedBytes - compact.EstimatedBytes);
        for (var bin = 0; bin < 8; bin++)
        {
            for (var y = -1; y <= height; y++)
            {
                for (var x = -1; x <= width; x++)
                {
                    Assert.Equal(full.At(bin, x, y), compact.At(bin, x, y));
                }
            }
        }
        Assert.Throws<InvalidOperationException>(() => compact.Row(0, 0, 0, 1).ToArray());
    }

    [Fact]
    public void OmittingLinearResponsesAvoidsTheirAllocationRatherThanOnlyReducingTheEstimate()
    {
        const int Width = 521;
        const int Height = 509;
        var bits = new byte[Width * Height];
        new Random(1823).NextBytes(bits);
        _ = OrientationMaps.Create(bits, Width, Height, 2, 4);
        _ = OrientationMaps.Create(bits, Width, Height, 2, 4, createLinearResponses: false);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var full = OrientationMaps.Create(bits, Width, Height, 2, 4);
        var fullAllocation = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread();
        var compact = OrientationMaps.Create(bits, Width, Height, 2, 4, createLinearResponses: false);
        var compactAllocation = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(fullAllocation - compactAllocation >= full.EstimatedBytes - compact.EstimatedBytes);
        GC.KeepAlive(full);
        GC.KeepAlive(compact);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompactSearchPreparationPreservesGradientsAndPublicPreparationKeepsLinearResponses(bool tiled)
    {
        var image = SyntheticScene.Create(163, 151, new MatchPose(81.3, 75.7, 0.13));
        var full = PreparedSearchImage.Create(image, 8, 3, tiled: tiled);
        var compact = PreparedSearchImage.CreateForSearch(image, 8, 3, true, default, tiled, null,
            createLinearResponses: false);
        Assert.Equal(full.LevelCount, compact.LevelCount);
        for (var level = 0; level < full.LevelCount; level++)
        {
            Assert.True(full.Maps[level].HasLinearResponses);
            Assert.False(compact.Maps[level].HasLinearResponses);
            Assert.Equal(full.Gradients[level].X, compact.Gradients[level].X);
            Assert.Equal(full.Gradients[level].Y, compact.Gradients[level].Y);
            Assert.Equal(full.Gradients[level].Magnitude, compact.Gradients[level].Magnitude);
            Assert.Equal(full.Gradients[level].Bits, compact.Gradients[level].Bits);
            Assert.Equal(full.Gradients[level].EdgePoints, compact.Gradients[level].EdgePoints);
        }
        var omittedBytes = full.Maps.Sum(map => 8L * map.Step * map.Step * map.PlaneLength);
        Assert.Equal(omittedBytes, full.EstimatedBytes - compact.EstimatedBytes);
    }
}
