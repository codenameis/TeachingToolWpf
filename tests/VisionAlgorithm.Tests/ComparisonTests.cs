using VisionAlgorithm.OpenSrc.Comparisons;
using VisionAlgorithm.OpenSrc.PatternMatching;

namespace VisionAlgorithm.Tests;

public sealed class ComparisonTests
{
    [Fact]
    public void BoldMatcherFindsRotatedAndScaledSyntheticShape()
    {
        var trainingImage = SyntheticScene.Create(96, 96, new MatchPose(48, 48, 0));
        var model = BoldTrainer.Train(trainingImage, new(ReadOnlyMemory<byte>.Empty, new(48, 48)), new());
        var expected = new MatchPose(101.3, 85.7, 13.4 * Math.PI / 180, 1.13);
        var searchImage = SyntheticScene.Create(193, 171, expected);

        var result = BoldMatcher.Search(searchImage, model, new());

        Assert.True(result.Matches.Length > 0, result.Diagnostics.ToString());
        var match = result.Matches[0];
        Assert.InRange(Math.Abs(match.Pose.X - expected.X), 0, 4);
        Assert.InRange(Math.Abs(match.Pose.Y - expected.Y), 0, 4);
        Assert.InRange(Math.Abs(match.Pose.Angle - expected.Angle), 0, 15 * Math.PI / 180);
        Assert.InRange(Math.Abs(match.Pose.Scale - expected.Scale), 0, 0.35);
        Assert.True(match.Inliers >= 3);
        Assert.Empty(BoldMatcher.Search(SyntheticScene.Create(193, 171), model, new()).Matches);
    }

    [Fact]
    public void BoldMatcherFindsTwoSeparatedInstances()
    {
        var trainingImage = SyntheticScene.Create(96, 96, new MatchPose(48, 48, 0));
        var model = BoldTrainer.Train(trainingImage, new(ReadOnlyMemory<byte>.Empty, new(48, 48)), new());
        var first = new MatchPose(65, 75, 0.08);
        var second = new MatchPose(180, 80, -0.13);
        var scene = SyntheticScene.Create(250, 160, first, second);

        var result = BoldMatcher.Search(scene, model, new());

        Assert.True(result.Matches.Length >= 2, result.Diagnostics.ToString());
        Assert.Contains(result.Matches, match => Math.Abs(match.Pose.X - first.X) < 10 && Math.Abs(match.Pose.Y - first.Y) < 10);
        Assert.Contains(result.Matches, match => Math.Abs(match.Pose.X - second.X) < 10 && Math.Abs(match.Pose.Y - second.Y) < 10);
    }

    [Fact]
    public void BoldMatcherReportsCoverageOnThreeInstanceBenchmarkScene()
    {
        var trainingImage = SyntheticScene.Create(96, 96, new MatchPose(48, 48, 0));
        var model = BoldTrainer.Train(trainingImage, new(ReadOnlyMemory<byte>.Empty, new(48, 48)), new());
        MatchPose[] expected =
        [
            new(103.3, 101.7, 0.234, 1.13),
            new(313.2, 210.4, -0.21, 0.93),
            new(508.6, 302.1, 0.08, 1.04)
        ];
        var scene = SyntheticScene.Create(641, 385, expected);

        var result = BoldMatcher.Search(scene, model, new());

        Assert.Equal(3, result.Matches.Length);
        foreach (var target in expected)
        {
            Assert.Contains(result.Matches, match => Math.Abs(match.Pose.X - target.X) < 10 &&
                Math.Abs(match.Pose.Y - target.Y) < 10);
        }
    }

    [Fact]
    public void BoldDescriptorIsInvariantToRigidRotationTranslationAndUniformScale()
    {
        LineSegment[] segments =
        [
            new(new(-18, -12), new(3, -9)),
            new(new(3, -9), new(13, 7)),
            new(new(13, 7), new(1, 20)),
            new(new(1, 20), new(-18, 14)),
            new(new(-11, -5), new(-2, 9)),
            new(new(-5, 5), new(8, 2)),
            new(new(5, -4), new(8, 11))
        ];
        const double Angle = 0.73;
        const double Scale = 1.7;
        var cos = Math.Cos(Angle);
        var sin = Math.Sin(Angle);
        var transformed = segments.Select(segment => new LineSegment(Transform(segment.Start), Transform(segment.End))).ToArray();

        var original = BoldDescriptor.Compute(segments, 0);
        var actual = BoldDescriptor.Compute(transformed, 0);

        Assert.Equal(original.Length, actual.Length);
        for (var i = 0; i < original.Length; i++)
        {
            Assert.Equal(original[i], actual[i], 5);
        }

        ImagePoint Transform(ImagePoint point)
            => new(30 + (Scale * ((cos * point.X) - (sin * point.Y))),
                -17 + (Scale * ((sin * point.X) + (cos * point.Y))));
    }

    [Fact]
    public void BoldMatcherHonorsCancellationAndPixelBudget()
    {
        var trainingImage = SyntheticScene.Create(96, 96, new MatchPose(48, 48, 0));
        var model = BoldTrainer.Train(trainingImage, new(ReadOnlyMemory<byte>.Empty, new(48, 48)), new());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => BoldMatcher.Search(trainingImage, model, new(), cancellation.Token));

        var oversized = new GrayImage(new byte[513 * 512], 513, 512, 513);
        Assert.Throws<ArgumentException>(() => BoldMatcher.Search(oversized, model, new()));
    }
}
