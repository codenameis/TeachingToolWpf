using VisionAlgorithm.OpenSrc.PatternMatching;

namespace VisionAlgorithm.Tests;

public sealed class PreparedCandidateTests
{
    [Theory]
    [InlineData(1, false)]
    [InlineData(4, true)]
    public void PreparedTemplatePreservesCandidateOrderScoresAndBudget(int parallelism, bool scale)
    {
        var model = ShapeTrainer.Train(SyntheticScene.Create(96, 96, new MatchPose(48, 48, 0)),
            new(ReadOnlyMemory<byte>.Empty, new(48, 48)), new()
            {
                MinimumAngleDegrees = -180,
                MaximumAngleDegrees = 180,
                AngleStepDegrees = 10,
                MinimumScale = scale ? .9 : 1,
                MaximumScale = scale ? 1.1 : 1
            });
        var scene = SyntheticScene.Create(193, 171, new MatchPose(101.3, 85.7, .234, scale ? 1.075 : 1));
        var prepared = PreparedSearchImage.Create(scene, 8, model.Levels.Length);
        var options = new SearchOptions { MaximumParallelism = parallelism, MaximumCandidates = 128 };
        var scalar = CandidateSearch.Find(prepared, model, options, default);
        var cached = CandidateSearch.Find(prepared, model, options, default, optimizedLocalSearch: true);
        Assert.Equal(scalar.Candidates, cached.Candidates);
        Assert.Equal(scalar.BudgetExceeded, cached.BudgetExceeded);
        Assert.Equal(scalar.EvaluatedTemplates, cached.EvaluatedTemplates);
    }
}
