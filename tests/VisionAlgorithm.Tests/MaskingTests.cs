using VisionAlgorithm.OpenSrc.PatternMatching;

namespace VisionAlgorithm.Tests;

public sealed class MaskingTests
{
    [Fact]
    public void IntentionalExclusionsDoNotConsumeVerificationBudgetOrSuppressValidTarget()
    {
        var training = SyntheticScene.Create(64, 64, new MatchPose(32, 32, 0));
        var model = ShapeTrainer.Train(training, new(ReadOnlyMemory<byte>.Empty, new(32, 32)), new()
        {
            PyramidLevels = 1,
            MinimumAngleDegrees = 0,
            MaximumAngleDegrees = 0
        });
        var scene = SyntheticScene.Create(192, 96, new MatchPose(48, 48, 0), new MatchPose(144, 48, 0));
        var excluded = new byte[192 * 96];
        for (var y = 0; y < 96; y++)
        {
            Array.Fill(excluded, (byte)255, y * 192, 96);
        }
        var masked = new GrayImage(scene.Pixels, scene.Width, scene.Height, scene.Stride, excluded);
        var prepared = PreparedSearchImage.Create(masked, model.Options.MinimumGradient, 1);
        Array.Clear(excluded);
        var batch = new CandidateBatch([new(new(48, 48, 0), 1, 0), new(new(144, 48, 0), 1, 0)], false, 1);
        var options = new SearchOptions { Verify = false, Refine = false, MaximumVerifiedCandidates = 1 };
        var result = ShapeMatcher.CompleteCandidates(prepared, model, options, batch, 0, default);
        Assert.False(result.Diagnostics.VerificationBudgetExceeded);
        Assert.Equal(144, Assert.Single(result.Matches).Pose.X);
        Assert.Throws<ArgumentException>(() => new GrayImage(scene.Pixels, scene.Width, scene.Height, scene.Stride, new byte[1]));
    }
}
