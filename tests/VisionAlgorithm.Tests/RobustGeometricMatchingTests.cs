using VisionAlgorithm.OpenSrc.PatternMatching;
using Xunit.Abstractions;

namespace VisionAlgorithm.Tests;

public sealed class RobustGeometricMatchingTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> PositiveCases()
        => RobustGeometricScene.PositiveScenarios.Select(scenario => new object[] { scenario.Name });

    public static IEnumerable<object[]> NegativeCases()
        => RobustGeometricScene.NegativeScenarios.Select(scenario => new object[] { scenario.Name });

    [Theory]
    [MemberData(nameof(PositiveCases))]
    public void FullTurnSearchRecoversUniquePoseUnderExplicitDisturbances(string name)
    {
        var scenario = RobustGeometricScene.PositiveScenarios.Single(value => value.Name == name);
        var model = RobustGeometricScene.Train();
        var result = new ShapeMatcher().Search(RobustGeometricScene.Create(scenario), model, RobustGeometricScene.PrecisionOptions());
        WriteDiagnostics(name, result);

        var match = Assert.Single(result.Matches);
        var expected = RobustGeometricScene.ExpectedPose(scenario);
        Assert.InRange(RobustGeometricScene.PositionError(match.Pose), 0, 0.75);
        Assert.InRange(RobustGeometricScene.AngleErrorDegrees(match.Pose, expected), 0, 1.5);
        Assert.InRange(Math.Abs(match.Pose.Scale - expected.Scale), 0, 0.025);
        AssertPrecisionQuality(match);
        Assert.False(result.Diagnostics.CandidateBudgetExceeded);
        Assert.False(result.Diagnostics.VerificationBudgetExceeded);
        Assert.False(result.Diagnostics.MatchLimitExceeded);
    }

    [Theory]
    [MemberData(nameof(NegativeCases))]
    public void FullTurnSearchRejectsAbsentOrSimilarShapes(string name)
    {
        var scenario = RobustGeometricScene.NegativeScenarios.Single(value => value.Name == name);
        var result = new ShapeMatcher().Search(RobustGeometricScene.Create(scenario), RobustGeometricScene.Train(),
            RobustGeometricScene.PrecisionOptions());
        WriteDiagnostics(name, result);

        Assert.Empty(result.Matches);
        Assert.False(result.Diagnostics.CandidateBudgetExceeded);
        Assert.False(result.Diagnostics.VerificationBudgetExceeded);
        Assert.False(result.Diagnostics.MatchLimitExceeded);
    }

    [Theory]
    [InlineData(ContrastPolarity.Preserve, false)]
    [InlineData(ContrastPolarity.IgnoreLocal, true)]
    public void FullTurnSearchAppliesContrastPolarity(ContrastPolarity polarity, bool shouldFind)
    {
        var scenario = new GeometricScenario("inverted", 137.4, 1.07, Inverted: true);
        var result = new ShapeMatcher().Search(RobustGeometricScene.Create(scenario), RobustGeometricScene.Train(polarity),
            RobustGeometricScene.PrecisionOptions());
        WriteDiagnostics($"inverted-{polarity}", result);

        Assert.False(result.Diagnostics.CandidateBudgetExceeded);
        Assert.False(result.Diagnostics.VerificationBudgetExceeded);
        Assert.False(result.Diagnostics.MatchLimitExceeded);

        if (!shouldFind)
        {
            Assert.Empty(result.Matches);
            return;
        }
        var match = Assert.Single(result.Matches);
        Assert.InRange(RobustGeometricScene.PositionError(match.Pose), 0, 0.75);
        Assert.InRange(RobustGeometricScene.AngleErrorDegrees(match.Pose, RobustGeometricScene.ExpectedPose(scenario)), 0, 1.5);
        Assert.InRange(Math.Abs(match.Pose.Scale - scenario.Scale), 0, 0.025);
        AssertPrecisionQuality(match);
    }

    [Theory]
    [InlineData("background-clutter")]
    [InlineData("occlusion-clutter-half-contrast")]
    public void DefaultBudgetExhaustionRemainsVisibleForClutter(string name)
    {
        var scenario = RobustGeometricScene.PositiveScenarios.Single(value => value.Name == name);
        var result = new ShapeMatcher().Search(RobustGeometricScene.Create(scenario), RobustGeometricScene.Train(), new());
        WriteDiagnostics(name, result);

        Assert.True(result.Diagnostics.CandidateBudgetExceeded);
        var match = Assert.Single(result.Matches);
        Assert.InRange(RobustGeometricScene.PositionError(match.Pose), 0, 0.75);
    }

    private void WriteDiagnostics(string name, MatchResult result)
    {
        output.WriteLine($"{name}: {result.Diagnostics}");
        foreach (var match in result.Matches)
        {
            output.WriteLine(match.ToString());
        }
    }

    private static void AssertPrecisionQuality(ShapeMatch match)
    {
        var quality = Assert.IsType<GeometricMatchQuality>(match.Geometry);
        Assert.InRange(quality.Coverage, 0.75, 1);
        Assert.InRange(quality.FitErrorPixels, 0, 1);
        Assert.InRange(quality.ClutterRatio, 0, 0.75);
        Assert.True(quality.MatchedFeatures <= quality.ModelFeatures);
    }
}
