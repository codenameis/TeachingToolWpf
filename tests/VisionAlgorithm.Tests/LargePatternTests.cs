using VisionAlgorithm.OpenSrc.PatternMatching;

namespace VisionAlgorithm.Tests;

public sealed class LargePatternTests
{
    [Fact]
    public void RefinementRejectsInvalidInitialPoseAndReportsOutputLimit()
    {
        var model = MatchingTests.Train();
        var scene = SyntheticScene.Create(225, 145, new MatchPose(50, 65, .1), new MatchPose(160, 80, -.2));
        var matcher = new ShapeMatcher();
        Assert.Throws<ArgumentException>(() => matcher.RefineCandidate(scene, model, new(50, 65, 0, 2), .8, new()));
        Assert.Throws<ArgumentException>(() => matcher.RefineCandidate(scene, model, new(double.NaN, 65, 0), .8, new()));
        Assert.Throws<ArgumentException>(() => matcher.RefineCandidate(scene, model, new(50, 65, 0), double.NaN, new()));
        var result = matcher.Search(scene, model, new()
        {
            MaximumMatches = 1,
            MaximumVerifiedCandidates = 4096
        });
        Assert.Single(result.Matches);
        Assert.True(result.Diagnostics.MatchLimitExceeded);
        Assert.False(result.Diagnostics.VerificationBudgetExceeded);
    }

    [Theory]
    [InlineData(3.4, 1, false)]
    [InlineData(-6.3, 1.03, false)]
    [InlineData(3.4, 1, true)]
    [InlineData(-6.3, 1.03, true)]
    public void LargePatternRefinesAngleAndScaleBetweenCoarseSamples(double degrees, double scale, bool refineCandidate)
    {
        var model = ShapeTrainer.Train(SyntheticScene.Create(640, 640, new MatchPose(320, 320, 0, 10)),
            new(ReadOnlyMemory<byte>.Empty, new(320, 320)), new()
            {
                PyramidLevels = 4,
                MinimumAngleDegrees = -20,
                MaximumAngleDegrees = 20,
                AngleStepDegrees = 10,
                MinimumScale = 1,
                MaximumScale = 1.1,
                ScaleStep = 0.1
            });
        var expected = new MatchPose(331.3, 311.7, degrees * Math.PI / 180, 10 * scale);
        var scene = SyntheticScene.Create(672, 672, expected);
        var initial = new MatchPose(Math.Round(expected.X / 8) * 8, Math.Round(expected.Y / 8) * 8,
            Math.Round(degrees / 2) * 2 * Math.PI / 180, Math.Round(scale / .05) * .05);
        var matcher = new ShapeMatcher();
        var result = refineCandidate ? matcher.RefineCandidate(scene, model, initial, .8, new()) : matcher.Search(scene, model, new());
        var match = Assert.Single(result.Matches);
        Assert.InRange(Math.Abs(match.Pose.X - expected.X), 0, 0.5);
        Assert.InRange(Math.Abs(match.Pose.Y - expected.Y), 0, 0.5);
        Assert.InRange(Math.Abs(match.Pose.Angle - expected.Angle), 0, 0.3 * Math.PI / 180);
        Assert.InRange(Math.Abs(match.Pose.Scale - scale), 0, 0.005);
        Assert.Equal(4, model.Levels[0].Features.Select(feature => (feature.X >= 0 ? 1 : 0) + (feature.Y >= 0 ? 2 : 0)).Distinct().Count());
    }

    [Fact]
    public void RegionProtectsCandidateBudgetFromDistantStrongerInstances()
    {
        var model = MatchingTests.Train();
        var scene = SyntheticScene.Create(400, 160, new MatchPose(50, 65, 0), new MatchPose(180, 65, 0), new MatchPose(320.3, 80.7, .12));
        var match = Assert.Single(new ShapeMatcher().Search(scene, model, new()
        {
            Region = new(new ImagePoint(320, 80), 8),
            MaximumCandidates = 1,
            MaximumVerifiedCandidates = 1
        }).Matches);
        Assert.InRange(match.Pose.X, 319.8, 320.8);
        Assert.InRange(match.Pose.Y, 80.2, 81.2);
    }
}
