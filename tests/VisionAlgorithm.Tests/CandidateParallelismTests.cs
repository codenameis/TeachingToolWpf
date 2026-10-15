using VisionAlgorithm.OpenSrc.PatternMatching;

namespace VisionAlgorithm.Tests;

public sealed class CandidateParallelismTests
{
    [Theory]
    [InlineData(12, 2, 1)]
    [InlineData(2048, 64, 16)]
    public void ParallelCandidatesPreserveExactMatchesAndBudgetWarnings(int maximumCandidates,
        int maximumVerifiedCandidates, int maximumMatches)
    {
        var model = ShapeTrainer.Train(SyntheticScene.Create(96, 96, new MatchPose(48, 48, 0)),
            new(ReadOnlyMemory<byte>.Empty, new(48, 48)), new()
            {
                BuildClutterModel = true
            });
        var scene = SyntheticScene.Create(255, 173,
            new MatchPose(48.2, 55.7, 0.13),
            new MatchPose(158.4, 55.2, -0.18),
            new MatchPose(109.3, 128.1, 0.09));
        var prepared = PreparedSearchImage.Create(scene, 8, model.Levels.Length);
        var options = new SearchOptions
        {
            CandidateThreshold = 0.45,
            MaximumCandidates = maximumCandidates,
            MaximumVerifiedCandidates = maximumVerifiedCandidates,
            MaximumMatches = maximumMatches,
            ClutterWeight = 1
        };
        var matcher = new ShapeMatcher();

        var sequential = matcher.Search(prepared, model, options);
        var parallel = matcher.Search(prepared, model, options with
        {
            MaximumParallelism = 4
        });

        Assert.NotEmpty(sequential.Matches);
        Assert.True(sequential.Diagnostics.Candidates > 1);
        Assert.Equal(sequential.Matches.ToArray(), parallel.Matches.ToArray());
        Assert.Equal(sequential.Diagnostics.Candidates, parallel.Diagnostics.Candidates);
        Assert.Equal(sequential.Diagnostics.TemplatesEvaluated, parallel.Diagnostics.TemplatesEvaluated);
        Assert.Equal(sequential.Diagnostics.CandidateBudgetExceeded, parallel.Diagnostics.CandidateBudgetExceeded);
        Assert.Equal(sequential.Diagnostics.VerificationBudgetExceeded, parallel.Diagnostics.VerificationBudgetExceeded);
        Assert.Equal(sequential.Diagnostics.MatchLimitExceeded, parallel.Diagnostics.MatchLimitExceeded);
        if (maximumCandidates == 12)
        {
            Assert.True(sequential.Diagnostics.CandidateBudgetExceeded);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void CandidateCancellationPreservesTheCallerToken(int maximumParallelism)
    {
        var model = ShapeTrainer.Train(SyntheticScene.Create(96, 96, new MatchPose(48, 48, 0)),
            new(ReadOnlyMemory<byte>.Empty, new(48, 48)), new()
            {
                BuildClutterModel = true
            });
        var prepared = PreparedSearchImage.Create(SyntheticScene.Create(163, 151, new MatchPose(81, 75, 0.15)),
            8, model.Levels.Length);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var error = Assert.Throws<OperationCanceledException>(() => CandidateSearch.Find(prepared, model,
            new()
            {
                MaximumParallelism = maximumParallelism
            }, cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
    }
}
