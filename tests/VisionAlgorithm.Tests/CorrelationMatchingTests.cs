using VisionAlgorithm.OpenSrc.PatternMatching;

namespace VisionAlgorithm.Tests;

public sealed class CorrelationMatchingTests
{
    [Fact]
    public void TranslationReturnsOriginalReferencePointAndNormalizedScore()
    {
        var pixels = Pattern();
        var model = CorrelationTemplate.Train(new(pixels, 24, 20, 24), new(ReadOnlyMemory<byte>.Empty, new(5.25, 8.5)));
        var scene = new byte[128 * 80];
        Paste(scene, 128, pixels, 17, 13);
        Paste(scene, 128, pixels, 83, 49);
        var result = CorrelationMatcher.Search(new(scene, 128, 80, 128), model, new()
        {
            MinimumScore = .99
        });
        Assert.False(result.Truncated);
        Assert.Equal(2, result.Matches.Length);
        Assert.Contains(result.Matches, match => match.ReferencePoint == new ImagePoint(22.25, 21.5) && match.Score > .999);
        Assert.Contains(result.Matches, match => match.ReferencePoint == new ImagePoint(88.25, 57.5) && match.Score > .999);
    }

    [Fact]
    public void MaskExcludesBackgroundAndZeroVarianceSearchDoesNotProduceMatches()
    {
        var pixels = Pattern();
        var mask = new byte[24 * 20];
        for (var y = 4; y < 16; y++)
        {
            Array.Fill(mask, (byte)255, (y * 24) + 5, 14);
        }
        var model = CorrelationTemplate.Train(new(pixels, 24, 20, 24), new(mask, new(12, 10)));
        var changed = pixels.ToArray();
        for (var i = 0; i < changed.Length; i++)
        {
            if (mask[i] == 0)
            {
                changed[i] = (byte)(255 - changed[i]);
            }
        }
        var result = CorrelationMatcher.Search(new(changed, 24, 20, 24), model, new()
        {
            MinimumScore = .99
        });
        Assert.Equal(new ImagePoint(12, 10), Assert.Single(result.Matches).ReferencePoint);
        var empty = CorrelationMatcher.Search(new(new byte[64 * 64], 64, 64, 64), model, new());
        Assert.Empty(empty.Matches);
    }

    [Fact]
    public void SearchRegionProtectsCandidateBudgetFromOutsideStrongerMatches()
    {
        var pixels = Pattern();
        var model = CorrelationTemplate.Train(new(pixels, 24, 20, 24), new(ReadOnlyMemory<byte>.Empty, new(12, 10)));
        var scene = new byte[160 * 40];
        Paste(scene, 160, pixels, 5, 10);
        Paste(scene, 160, pixels, 55, 10);
        Paste(scene, 160, pixels, 105, 10);
        var result = CorrelationMatcher.Search(new(scene, 160, 40, 160), model, new()
        {
            MinimumScore = .99,
            MaximumCandidates = 1,
            Region = new(new ImagePoint(117, 20), 8)
        });
        Assert.False(result.Truncated);
        Assert.Equal(new ImagePoint(117, 20), Assert.Single(result.Matches).ReferencePoint);
    }

    [Fact]
    public void TrainingOwnsPixelDataAndRejectsFlatOrEmptyMasks()
    {
        var pixels = Pattern();
        var original = pixels.ToArray();
        var model = CorrelationTemplate.Train(new(pixels, 24, 20, 24), new(ReadOnlyMemory<byte>.Empty, new(12, 10)));
        Array.Clear(pixels);
        Assert.Single(CorrelationMatcher.Search(new(original, 24, 20, 24), model, new()
        {
            MinimumScore = .99
        }).Matches);
        Assert.Throws<ArgumentException>(() => CorrelationTemplate.Train(new(pixels, 24, 20, 24), new(ReadOnlyMemory<byte>.Empty, new(12, 10))));
        Assert.Throws<ArgumentException>(() => CorrelationTemplate.Train(new(original, 24, 20, 24), new(pixels, new(12, 10))));
    }

    [Fact]
    public void PaddedStrideAndCancellationFollowGrayImageContract()
    {
        var pixels = Pattern();
        var padded = new byte[32 * 20];
        for (var y = 0; y < 20; y++)
        {
            pixels.AsSpan(y * 24, 24).CopyTo(padded.AsSpan(y * 32));
        }
        var model = CorrelationTemplate.Train(new(padded, 24, 20, 32), new(ReadOnlyMemory<byte>.Empty, new(12, 10)));
        Assert.Single(CorrelationMatcher.Search(new(padded, 24, 20, 32), model, new()
        {
            MinimumScore = .99
        }).Matches);
        Assert.Throws<OperationCanceledException>(() => CorrelationMatcher.Search(new(padded, 24, 20, 32), model, new(), new(true)));
    }

    [Fact]
    public void SmallViewDoesNotCopyTheEntireBackingImage()
    {
        var pixels = Pattern();
        var backing = new byte[8 * 1024 * 1024];
        pixels.CopyTo(backing, 0);
        var model = CorrelationTemplate.Train(new(pixels, 24, 20, 24), new(ReadOnlyMemory<byte>.Empty, new(12, 10)));
        var view = new GrayImage(backing, 24, 20, 24);
        CorrelationMatcher.Search(view, model, new());
        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Single(CorrelationMatcher.Search(view, model, new()).Matches);
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 1024 * 1024);
    }

    private static byte[] Pattern()
    {
        var pixels = new byte[24 * 20];
        new Random(407).NextBytes(pixels);
        return pixels;
    }

    private static void Paste(byte[] scene, int stride, byte[] pixels, int left, int top)
    {
        for (var y = 0; y < 20; y++)
        {
            pixels.AsSpan(y * 24, 24).CopyTo(scene.AsSpan(((top + y) * stride) + left));
        }
    }
}
