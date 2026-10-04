using VisionAlgorithm.OpenSrc.PatternMatching;

namespace VisionAlgorithm.Tests;

public sealed class ContinuousVerifierTests
{
    [Theory]
    [InlineData(0.75, -13.4)]
    [InlineData(1, 13.4)]
    [InlineData(1.25, -17.2)]
    public void FixedScaleStillOptimizesTranslationAndRotation(double scale, double degrees)
    {
        var model = ShapeTrainer.Train(SyntheticScene.Create(96, 96, new MatchPose(48, 48, 0)),
            new(ReadOnlyMemory<byte>.Empty, new(48, 48)), new()
            {
                MinimumScale = scale,
                MaximumScale = scale
            });
        var expected = new MatchPose(101.3, 85.7, degrees * Math.PI / 180, scale);
        var gradient = GradientImage.Create(SyntheticScene.Create(193, 171, expected), 8, default);
        var initial = expected with
        {
            X = expected.X + 1.25,
            Y = expected.Y - 1.25,
            Angle = expected.Angle + (1.5 * Math.PI / 180)
        };
        var initialScore = ContinuousVerifier.Score(gradient, model, initial);

        var result = ContinuousVerifier.Optimize(gradient, model, initial, new(), default);

        Assert.Equal(scale, result.Pose.Scale);
        Assert.True(result.Score.Score > initialScore.Score);
        Assert.InRange(Math.Abs(result.Pose.X - expected.X), 0, 0.5);
        Assert.InRange(Math.Abs(result.Pose.Y - expected.Y), 0, 0.5);
        Assert.InRange(Math.Abs(result.Pose.Angle - expected.Angle), 0, Math.PI / 180);
        Assert.Equal(ContinuousVerifier.Score(gradient, model, result.Pose), result.Score);
    }
}
