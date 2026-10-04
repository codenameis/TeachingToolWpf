using VisionAlgorithm.OpenSrc.PatternMatching;

namespace VisionAlgorithm.Tests;

public sealed class CorrelationLimitTests
{
    [Theory]
    [InlineData(16, false)]
    [InlineData(17, true)]
    public void OutputLimitReportsOnlyAdditionalAcceptedMatches(int patternCount, bool expectedLimitExceeded)
    {
        const int Width = 1024;
        const int Height = 40;
        var pattern = CreatePattern();
        var model = CorrelationTemplate.Train(new(pattern, 24, 20, 24), new(ReadOnlyMemory<byte>.Empty, new(12, 10)));
        var scene = new byte[Width * Height];
        for (var index = 0; index < patternCount; index++)
        {
            Paste(scene, Width, pattern, 10 + (index * 50), 10);
        }

        var result = CorrelationMatcher.Search(new(scene, Width, Height, Width), model, new()
        {
            MinimumScore = .99,
            MaximumMatches = 16
        });

        Assert.False(result.Truncated);
        Assert.Equal(16, result.Matches.Length);
        Assert.Equal(expectedLimitExceeded, result.MatchLimitExceeded);
    }

    [Fact]
    public void CandidateBudgetRemainsSeparateFromTheOutputLimit()
    {
        const int Width = 240;
        const int Height = 40;
        var pattern = CreatePattern();
        var model = CorrelationTemplate.Train(new(pattern, 24, 20, 24), new(ReadOnlyMemory<byte>.Empty, new(12, 10)));
        var scene = new byte[Width * Height];
        for (var index = 0; index < 3; index++)
        {
            Paste(scene, Width, pattern, 10 + (index * 70), 10);
        }

        var result = CorrelationMatcher.Search(new(scene, Width, Height, Width), model, new()
        {
            MinimumScore = .99,
            MaximumCandidates = 2,
            MaximumMatches = 3
        });

        Assert.True(result.Truncated);
        Assert.False(result.MatchLimitExceeded);
        Assert.Equal(2, result.Matches.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SuppressionDistinguishesOverlappingCandidatesFromAdditionalMatches(bool additionalMatch)
    {
        var pattern = CreatePattern();
        var model = CorrelationTemplate.Train(new(pattern, 24, 20, 24), new(ReadOnlyMemory<byte>.Empty, new(12, 10)));
        var candidates = new List<CorrelationMatch>
        {
            new(new(13, 10), .95),
            new(new(12, 10), 1),
            new(new(11, 10), .9)
        };
        if (additionalMatch)
        {
            candidates.Add(new(new(112, 10), .8));
        }

        var result = CorrelationMatcher.SuppressWithDiagnostics(model, candidates, new()
        {
            MaximumMatches = 1
        });

        Assert.Equal(new ImagePoint(12, 10), Assert.Single(result.Matches).ReferencePoint);
        Assert.Equal(additionalMatch, result.MatchLimitExceeded);
    }

    private static byte[] CreatePattern()
    {
        var pattern = new byte[24 * 20];
        new Random(641).NextBytes(pattern);
        return pattern;
    }

    private static void Paste(byte[] scene, int stride, byte[] pattern, int left, int top)
    {
        for (var row = 0; row < 20; row++)
        {
            pattern.AsSpan(row * 24, 24).CopyTo(scene.AsSpan(((top + row) * stride) + left));
        }
    }
}
