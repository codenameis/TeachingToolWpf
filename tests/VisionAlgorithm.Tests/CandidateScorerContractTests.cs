using System.Collections.Immutable;
using VisionAlgorithm.OpenSrc.PatternMatching;

namespace VisionAlgorithm.Tests;

public sealed class CandidateScorerContractTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InjectedIntegerScoresPreserveExactCandidatesAndBudgetWarnings(bool grouping)
    {
        var model = MatchingTests.Train();
        var source = SyntheticScene.Create(193, 171,
            new MatchPose(48.2, 55.7, 0.13), new MatchPose(131.4, 109.2, -0.18));
        var image = PreparedSearchImage.Create(source, 8, model.Levels.Length);
        var compact = PreparedSearchImage.CreateForSearch(source, 8, model.Levels.Length, true,
            default, false, null, createLinearResponses: false);
        var options = new SearchOptions
        {
            CandidateThreshold = 0.45,
            MaximumCandidates = 12,
            GroupTemplates = grouping
        };
        var provider = new ReferenceScorer();

        var expected = CandidateSearch.Find(image, model, options, default);
        var actual = CandidateSearch.Find(compact, model, options, default, provider);

        Assert.NotEmpty(expected.Candidates);
        Assert.Equal(expected.Candidates, actual.Candidates);
        Assert.Equal(expected.BudgetExceeded, actual.BudgetExceeded);
        Assert.Equal(expected.EvaluatedTemplates, actual.EvaluatedTemplates);
        Assert.True(provider.Calls > 0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void IncorrectScorerOutputIsRejectedBeforeCandidateAccess(int invalidOutput)
    {
        var model = MatchingTests.Train();
        var image = PreparedSearchImage.Create(SyntheticScene.Create(96, 96, new MatchPose(48, 48, 0)), 8, model.Levels.Length);

        Assert.Throws<InvalidOperationException>(() => CandidateSearch.Find(image, model, new(), default,
            new ReferenceScorer { InvalidOutput = invalidOutput }));
    }

    private sealed class ReferenceScorer : IShapeCandidateScorer
    {
        public int Calls
        {
            get; private set;
        }
        public int InvalidOutput { get; init; } = -1;
        public CandidateScoringDiagnostics CandidateDiagnostics => new("Test", "Integer reference", Calls, 0, 0, 0);

        public ShapeCandidateScoreBatch Score(ShapeCandidateScoreRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (InvalidOutput == 0)
            {
                return null!;
            }
            var width = request.GridWidth;
            var height = request.GridHeight;
            var count = width * height;
            var scores = new ushort[count * request.Templates.Length];
            if (InvalidOutput > 0)
            {
                if (InvalidOutput == 5)
                {
                    scores[^1] = ushort.MaxValue;
                }
                return new(width + (InvalidOutput == 1 ? 1 : 0), height + (InvalidOutput == 2 ? 1 : 0),
                    request.Templates.Length + (InvalidOutput == 3 ? 1 : 0), InvalidOutput == 4 ? [] : scores);
            }
            var map = OrientationMaps.Create(request.OrientationBits.ToArray(), request.Width, request.Height,
                request.Radius, request.Step, cancellationToken);
            for (var template = 0; template < request.Templates.Length; template++)
            {
                var single = new ushort[count];
                var features = request.Templates[template].Select(feature => new QuantizedFeature(feature.X, feature.Y, feature.Bin)).ToImmutableArray();
                CandidateSearch.Accumulate(map, features, single, width, height, false, cancellationToken);
                single.CopyTo(scores, template * count);
            }
            return new(width, height, request.Templates.Length, scores);
        }
    }
}
