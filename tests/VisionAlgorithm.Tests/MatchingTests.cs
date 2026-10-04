using VisionAlgorithm.OpenSrc.PatternMatching;

namespace VisionAlgorithm.Tests;

public sealed class MatchingTests
{
    [Theory]
    [InlineData(101, 85, 0, 1)]
    [InlineData(101.3, 85.7, 13.4, 1)]
    [InlineData(101.3, 85.7, -17.2, 1.13)]
    public void RecoversIndependentFractionalPose(double x, double y, double degrees, double scale)
    {
        var model = Train();
        var expected = new MatchPose(x, y, degrees * Math.PI / 180, scale);
        var result = new ShapeMatcher().Search(SyntheticScene.Create(193, 171, expected), model, new());
        var match = Assert.Single(result.Matches);
        Assert.True(Math.Abs(x - match.Pose.X) < 0.35, $"X {match}");
        Assert.True(Math.Abs(y - match.Pose.Y) < 0.35, $"Y {match}");
        Assert.True(Math.Abs(expected.Angle - match.Pose.Angle) < 0.7 * Math.PI / 180, $"angle {match}");
        Assert.True(Math.Abs(scale - match.Pose.Scale) < 0.02, $"scale {match}");
        Assert.Contains(match.Refinement.Status, new[] { RefinementStatus.Converged, RefinementStatus.IterationLimit });
    }

    [Fact]
    public void JacobianMatchesIndependentFiniteDifference()
    {
        var feature = new ModelFeature(12.3, -7.1, 0.6, 0.8);
        var edge = new EdgePoint(26, 11, 0.6, 0.8, 50);
        var pose = new MatchPose(15, 22, 0.3, 1.2);
        var (_, jacobian) = PointToLineRefiner.ResidualAndJacobian(feature, edge, pose);
        double Residual(MatchPose p) => (0.6 * (p.X + (p.Scale * ((12.3 * Math.Cos(p.Angle)) + (7.1 * Math.Sin(p.Angle)))) - 26)) +
            (0.8 * (p.Y + (p.Scale * ((12.3 * Math.Sin(p.Angle)) - (7.1 * Math.Cos(p.Angle)))) - 11));
        const double Epsilon = 1e-6;
        for (var dimension = 0; dimension < 4; dimension++)
        {
            MatchPose Perturb(double delta) => dimension switch
            {
                0 => pose with { X = pose.X + delta },
                1 => pose with { Y = pose.Y + delta },
                2 => pose with { Angle = pose.Angle + delta },
                _ => pose with { Scale = pose.Scale * Math.Exp(delta) }
            };
            Assert.Equal((Residual(Perturb(Epsilon)) - Residual(Perturb(-Epsilon))) / (2 * Epsilon), jacobian[dimension], 6);
        }
    }

    [Fact]
    public void MultipleInstancesAndReferencePointSearchRegion()
    {
        var model = Train();
        var scene = SyntheticScene.Create(225, 145, new MatchPose(50, 65, 0.1), new MatchPose(160, 80, -0.2));
        var matcher = new ShapeMatcher();
        var all = matcher.Search(scene, model, new());
        Assert.Equal(2, all.Matches.Length);
        var limited = matcher.Search(scene, model, new()
        {
            Region = new([new(155, 75), new(165, 75), new(165, 85), new(155, 85)])
        });
        Assert.Single(limited.Matches);
        Assert.InRange(limited.Matches[0].Pose.X, 159.5, 160.5);
    }

    [Fact]
    public void MatchAngleIsAbsoluteImageAngleWhenTrainingRoiIsRotated()
    {
        const double TrainingAngle = 24;
        const double TargetAngle = 29;
        var trainingImage = SyntheticScene.Create(128, 128, new MatchPose(64, 64, TrainingAngle * Math.PI / 180));
        var searchImage = SyntheticScene.Create(128, 128, new MatchPose(64, 64, TargetAngle * Math.PI / 180));
        var model = ShapeTrainer.Train(trainingImage, new(ReadOnlyMemory<byte>.Empty, new(64, 64), TrainingAngle * Math.PI / 180),
            new()
            {
                MinimumAngleDegrees = -20,
                MaximumAngleDegrees = 20,
                AngleStepDegrees = 5
            });

        var match = Assert.Single(new ShapeMatcher().Search(searchImage, model, new()).Matches);

        Assert.InRange(Math.Abs(match.Pose.Angle - (TargetAngle * Math.PI / 180)), 0, 0.7 * Math.PI / 180);
    }

    internal static ShapeModel Train(ContrastPolarity polarity = ContrastPolarity.Preserve)
        => ShapeTrainer.Train(SyntheticScene.Create(96, 96, new MatchPose(48, 48, 0)),
            new(ReadOnlyMemory<byte>.Empty, new(48, 48)), new()
            {
                MinimumScale = 0.9,
                MaximumScale = 1.2,
                Polarity = polarity
            });
}
