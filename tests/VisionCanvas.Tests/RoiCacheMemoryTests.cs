using System.Runtime.CompilerServices;
using VisionCanvas.Rendering;

namespace TeachingToolWpf.Imaging.Tests;

public class RoiCacheMemoryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ZoomLevelsShareSourceUntilTheLastLevelIsEvicted(bool removeDetailedFirst)
    {
        var memory = new RoiCacheMemory();
        var identity = new object();
        const int SourceCount = 2048;
        const long PolygonBytes = 2048 * 17 * 16;
        const long SourceBytes = (SourceCount * 256L) + PolygonBytes;
        const long DetailedBytes = (2 * 65536) + (SourceCount * sizeof(uint));
        const long SummaryBytes = 65536 + (32 * sizeof(uint));
        memory.Add(identity, SourceCount, PolygonBytes, DetailedBytes);
        memory.Add(identity, SourceCount, PolygonBytes, SummaryBytes);
        Assert.Equal(SourceBytes + DetailedBytes + SummaryBytes, memory.Bytes);

        memory.Remove(identity, removeDetailedFirst ? DetailedBytes : SummaryBytes);
        Assert.Equal(SourceBytes + (removeDetailedFirst ? SummaryBytes : DetailedBytes), memory.Bytes);
        memory.Remove(identity, removeDetailedFirst ? SummaryBytes : DetailedBytes);
        Assert.Equal(0, memory.Bytes);
    }

    [Fact]
    public void SeparateSpatialIdentitiesKeepSeparateSourceCostsEvenWhenValuesAreEqual()
    {
        var memory = new RoiCacheMemory();
        var first = new EqualIdentity(1);
        var second = new EqualIdentity(1);
        Assert.Equal(first, second);
        Assert.NotSame(first, second);
        memory.Add(first, 100, 400, 65536);
        memory.Add(second, 100, 400, 65536);
        Assert.Equal(2 * ((100 * 256L) + 400 + 65536), memory.Bytes);

        memory.Remove(first, 65536);
        Assert.Equal((100 * 256L) + 400 + 65536, memory.Bytes);
        memory.Remove(second, 65536);
        Assert.Equal(0, memory.Bytes);
    }

    [Fact]
    public void TwoZoomLevelsOfLargeDocumentFitBudgetWhenOnlyTheirSharedSourceWasOvercounted()
    {
        var memory = new RoiCacheMemory();
        const int RoiCount = 271256;
        const long Budget = 128L * 1024 * 1024;
        var previouslyCountedBytes = 0L;
        for (var remaining = RoiCount; remaining > 0;)
        {
            var count = Math.Min(2048, remaining);
            var identity = new object();
            var ownedBytes = 65536 + (count * sizeof(uint));
            memory.Add(identity, count, 0, ownedBytes);
            memory.Add(identity, count, 0, ownedBytes);
            previouslyCountedBytes += 2 * ((count * 256L) + ownedBytes);
            remaining -= count;
        }

        Assert.True(previouslyCountedBytes > Budget);
        Assert.True(memory.Bytes <= Budget);
        Assert.Equal(previouslyCountedBytes - (RoiCount * 256L), memory.Bytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LastEvictionOrClearReleasesTheSpatialIdentity(bool clear)
    {
        var (memory, identity) = ReleaseIdentity(clear);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(identity.TryGetTarget(out _));
        Assert.Equal(0, memory.Bytes);
        GC.KeepAlive(memory);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (RoiCacheMemory Memory, WeakReference<object> Identity) ReleaseIdentity(bool clear)
    {
        var memory = new RoiCacheMemory();
        var identity = new object();
        memory.Add(identity, 2048, 4096, 65536);
        memory.Add(identity, 2048, 4096, 131072);
        memory.Remove(identity, 65536);
        if (clear)
        {
            memory.Clear();
        }
        else
        {
            memory.Remove(identity, 131072);
        }
        return (memory, new(identity));
    }

    private sealed record EqualIdentity(int Value);
}
