using VisionAlgorithm.OpenSrc.PatternMatching;

namespace VisionAlgorithm.Tests;

public sealed class VerifierBoundTests
{
    [Fact]
    public void SharedCoordinateInterpolationPreservesIndependentPlaneSamples()
    {
        const int Width = 173;
        const int Height = 157;
        var pixels = new byte[Width * Height];
        var random = new Random(43817);
        random.NextBytes(pixels);
        var gradient = GradientImage.Create(new(pixels, Width, Height, Width), 8, default);

        for (var index = 0; index < 4096; index++)
        {
            var x = index < Width + 2 ? index - 1 : (random.NextDouble() * (Width + 2)) - 1;
            var y = index < Height + 2 ? index - 1 : (random.NextDouble() * (Height + 2)) - 1;
            gradient.SampleDirection(x, y, out var actualX, out var actualY);
            Assert.Equal(GradientImage.Sample(gradient.X, Width, Height, x, y), actualX);
            Assert.Equal(GradientImage.Sample(gradient.Y, Width, Height, x, y), actualY);
            Assert.True(Math.Sqrt(((double)actualX * actualX) + ((double)actualY * actualY)) <= gradient.DirectionLengthUpperBound);
        }
    }

    [Theory]
    [InlineData(ContrastPolarity.Preserve, false, false)]
    [InlineData(ContrastPolarity.Preserve, true, false)]
    [InlineData(ContrastPolarity.IgnoreLocal, false, false)]
    [InlineData(ContrastPolarity.IgnoreLocal, true, true)]
    public void SafeBoundPreservesEveryWinningPoseAndScore(ContrastPolarity polarity, bool scaling, bool noise)
    {
        var model = ShapeTrainer.Train(SyntheticScene.Create(96, 96, new MatchPose(48, 48, 0)),
            new(ReadOnlyMemory<byte>.Empty, new(48, 48)), new()
            {
                Polarity = polarity,
                MinimumScale = scaling ? 0.85 : 1,
                MaximumScale = scaling ? 1.15 : 1,
                ScaleStep = 0.05
            });
        var scene = SyntheticScene.Create(193, 171, new MatchPose(101.3, 85.7, 0.234, scaling ? 1.075 : 1));
        if (noise)
        {
            var bytes = scene.Pixels.ToArray();
            new Random(437).NextBytes(bytes);
            scene = new(bytes, scene.Width, scene.Height, scene.Stride);
        }
        var image = GradientImage.Create(scene, 8, default);
        for (var seed = 0; seed < 12; seed++)
        {
            var pose = new MatchPose(97.1 + seed, 91.3 - seed, -0.25 + (seed * 0.045), scaling ? 0.88 + (seed * 0.02) : 1);
            var expected = OptimizeWithoutPruning(image, model, pose);
            var actual = ContinuousVerifier.Optimize(image, model, pose, new(), default);

            Assert.Equal(expected.Pose, actual.Pose);
            Assert.Equal(expected.Score, actual.Score);
        }
    }

    private static (MatchPose Pose, VerifiedScore Score) OptimizeWithoutPruning(GradientImage image, ShapeModel model, MatchPose initial)
    {
        var options = new SearchOptions();
        var pose = initial;
        var best = ContinuousVerifier.Score(image, model, pose);
        var radius = model.Levels[0].Features.Max(feature => Math.Sqrt((feature.X * feature.X) + (feature.Y * feature.Y)));
        var rotation = Math.Min(model.Options.AngleStepDegrees * Math.PI / 360, 2 / (radius * pose.Scale));
        var scale = model.Options.MaximumScale > model.Options.MinimumScale ? Math.Min(model.Options.ScaleStep / 2, 2 / radius) : 0;
        for (var resolution = 0; resolution < 5; resolution++)
        {
            var translation = 2d / (1 << resolution);
            for (var iteration = 0; iteration < 5; iteration++)
            {
                var changed = false;
                for (var dimension = 0; dimension < 4; dimension++)
                {
                    if (dimension == 3 && scale == 0)
                    {
                        continue;
                    }
                    var original = pose;
                    for (var sign = -1; sign <= 1; sign += 2)
                    {
                        var proposal = dimension switch
                        {
                            0 => original with { X = original.X + (sign * translation) },
                            1 => original with { Y = original.Y + (sign * translation) },
                            2 => original with { Angle = original.Angle + (sign * rotation) },
                            _ => original with { Scale = original.Scale + (sign * scale) }
                        };
                        if (!ContinuousVerifier.Allowed(proposal, model, options))
                        {
                            continue;
                        }
                        var score = ContinuousVerifier.Score(image, model, proposal);
                        if (score.Score > best.Score + 1e-10)
                        {
                            pose = proposal;
                            best = score;
                            changed = true;
                        }
                    }
                }
                if (!changed)
                {
                    break;
                }
            }
            rotation /= 2;
            scale /= 2;
        }
        return (pose, best);
    }
}
