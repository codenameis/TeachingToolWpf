using VisionAlgorithm.OpenSrc.PatternMatching;

namespace VisionAlgorithm.Tests;

public sealed class VerificationCandidateSchedulerTests
{
    [Fact]
    public void CandidatesWithinBudgetKeepTheirExactOrder()
    {
        var model = RobustShapeScene.Train();
        var candidates = new[] { Candidate(10), Candidate(10.1), Candidate(80) };
        Assert.Equal(candidates, VerificationCandidateScheduler.Select(candidates, model, 3, default));
    }

    [Fact]
    public void DifferentPositionsRotationsAndScalesReceiveBudgetBeforeNearDuplicates()
    {
        var model = RobustShapeScene.Train();
        var candidates = new[]
        {
            Candidate(100),
            Candidate(100.1),
            Candidate(100.2),
            Candidate(105),
            Candidate(100, Math.PI / 6),
            Candidate(100, 0, 1.2)
        };
        var selected = VerificationCandidateScheduler.Select(candidates, model, 4, default);
        Assert.Equal(new[] { candidates[0], candidates[3], candidates[4], candidates[5] }, selected);
    }

    [Fact]
    public void DeferredNearPosesArePreservedForRemainingBudget()
    {
        var model = RobustShapeScene.Train();
        var candidates = new[] { Candidate(10), Candidate(10.1), Candidate(10.2), Candidate(80), Candidate(80.1) };
        var original = candidates.ToArray();
        var selected = VerificationCandidateScheduler.Select(candidates, model, 4, default);
        Assert.Equal(new[] { candidates[0], candidates[3], candidates[1], candidates[2] }, selected);
        Assert.Equal(original, candidates);
    }

    [Fact]
    public void StandardBudgetPreservesSixteenNearbyRotatedScaledPatterns()
    {
        var training = SyntheticScene.Create(56, 56, new MatchPose(28, 28, 0));
        var model = ShapeTrainer.Train(training, new(ReadOnlyMemory<byte>.Empty, new(28, 28)),
            new()
            {
                MinimumScale = .9,
                MaximumScale = 1.2,
                BuildClutterModel = true
            });
        var expected = new MatchPose(32, 32, 13.4 * Math.PI / 180, 1.07);
        var tile = SyntheticScene.Create(64, 64, expected);
        var result = new ShapeMatcher().Search(RepeatTile(tile, 4, 4), model, new()
        {
            ClutterWeight = 1,
            MaximumMatches = 1000
        });
        Assert.Equal(16, result.Matches.Length);
        for (var y = 0; y < 4; y++)
        {
            for (var x = 0; x < 4; x++)
            {
                var match = Assert.Single(result.Matches.Where(match => Math.Abs(match.Pose.X - ((x * 64) + 32)) < .75 &&
                    Math.Abs(match.Pose.Y - ((y * 64) + 32)) < .75));
                Assert.InRange(Math.Abs(Math.IEEERemainder(match.Pose.Angle - expected.Angle, 2 * Math.PI)) * 180 / Math.PI, 0, 1.5);
                Assert.InRange(Math.Abs(match.Pose.Scale - expected.Scale), 0, .025);
            }
        }
    }

    [Fact]
    public void NearbyPosesAcrossCellBoundaryDoNotConsumeFirstPassTwice()
    {
        var model = RobustShapeScene.Train();
        var candidates = new[] { Candidate(99.9), Candidate(100.1), Candidate(160) };
        Assert.Equal(new[] { candidates[0], candidates[2] }, VerificationCandidateScheduler.Select(candidates, model, 2, default));
    }

    [Fact]
    public void CancellationIsObservedBeforeScheduling()
    {
        var model = RobustShapeScene.Train();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => VerificationCandidateScheduler.Select([Candidate(100)], model, 1, cancellation.Token));
    }

    [Fact]
    public void StandardBudgetFindsFortyRepeatedPatternsWithoutRelaxingAcceptance()
    {
        var training = SyntheticScene.Create(96, 96, new MatchPose(48, 48, 0));
        var model = ShapeTrainer.Train(training, new(ReadOnlyMemory<byte>.Empty, new(48, 48)),
            new()
            {
                BuildClutterModel = true
            });
        var image = RepeatTile(training, 8, 5);
        var result = new ShapeMatcher().Search(image, model, new()
        {
            ClutterWeight = 1,
            MaximumMatches = 1000
        });
        Assert.Equal(40, result.Matches.Length);
        Assert.False(result.Diagnostics.CandidateBudgetExceeded);
        Assert.True(result.Diagnostics.VerificationBudgetExceeded);
        for (var y = 0; y < 5; y++)
        {
            for (var x = 0; x < 8; x++)
            {
                var expectedX = (x * 96) + 48;
                var expectedY = (y * 96) + 48;
                var match = Assert.Single(result.Matches.Where(match => Math.Abs(match.Pose.X - expectedX) < .75 &&
                    Math.Abs(match.Pose.Y - expectedY) < .75));
                Assert.InRange(match.Score, .72, 1);
                Assert.InRange(match.Support, .6, 1);
                Assert.InRange(Math.Abs(match.Pose.Angle) * 180 / Math.PI, 0, 1.5);
            }
        }
    }

    [Fact]
    public void StandardBudgetPreservesOccludedPoseWithRefinementEnabled()
    {
        var model = RobustShapeScene.Train();
        var scenario = RobustShapeScene.PositiveScenarios.Single(scenario => scenario.Name == "right-quarter-occlusion");
        var result = new ShapeMatcher().Search(RobustShapeScene.Create(scenario), model, new()
        {
            ClutterWeight = 1
        });
        var match = Assert.Single(result.Matches);
        Assert.InRange(RobustShapeScene.PositionError(match.Pose), 0, .75);
        Assert.InRange(RobustShapeScene.AngleErrorDegrees(match.Pose, RobustShapeScene.ExpectedPose(scenario)), 0, 1.5);
        Assert.InRange(Math.Abs(match.Pose.Scale - scenario.Scale), 0, .025);
    }

    private static PoseCandidate Candidate(double x, double angle = 0, double scale = 1)
        => new(new(x, 100, angle, scale), 1, 0);

    private static GrayImage RepeatTile(GrayImage tile, int columns, int rows)
    {
        var width = tile.Width * columns;
        var height = tile.Height * rows;
        var pixels = new byte[width * height];
        for (var tileY = 0; tileY < rows; tileY++)
        {
            for (var tileX = 0; tileX < columns; tileX++)
            {
                for (var y = 0; y < tile.Height; y++)
                {
                    tile.Pixels.Span.Slice(y * tile.Stride, tile.Width).CopyTo(
                        pixels.AsSpan((((tileY * tile.Height) + y) * width) + (tileX * tile.Width), tile.Width));
                }
            }
        }
        return new(pixels, width, height, width);
    }
}
