using VisionAlgorithm.OpenSrc.PatternMatching;

namespace VisionAlgorithm.Tests;

public sealed class PreparedEdgeSelectionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SkippingEdgesPreservesEveryGradientPlaneWithPaddedStride(bool tiled)
    {
        const int Width = 269;
        const int Height = 259;
        const int Stride = 280;
        var pixels = new byte[Stride * Height];
        new Random(14317).NextBytes(pixels);
        var image = new GrayImage(pixels, Width, Height, Stride);
        var full = tiled ? GradientImage.CreateTiled(image, 8, default) : GradientImage.Create(image, 8, default);
        var withoutEdges = tiled ? GradientImage.CreateTiled(image, 8, default, extractEdges: false)
            : GradientImage.Create(image, 8, default, extractEdges: false);

        AssertPlanesEqual(full, withoutEdges);
        Assert.NotEmpty(full.EdgePoints);
        Assert.Empty(withoutEdges.EdgePoints);
        Assert.Equal(((long)Width * Height * 4) + (full.EdgePoints.LongLength * 40),
            full.EstimatedBytes - withoutEdges.EstimatedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PublicPreparationRetainsOriginalEdgesAndExactHigherLevelGradients(bool tiled)
    {
        var current = SyntheticScene.Create(163, 151, new MatchPose(81.3, 75.7, 0.13));
        var prepared = PreparedSearchImage.Create(current, 8, 3, tiled: tiled);

        for (var level = 0; level < prepared.LevelCount; level++)
        {
            var full = tiled ? GradientImage.CreateTiled(current, 8, default) : GradientImage.Create(current, 8, default);
            AssertPlanesEqual(full, prepared.Gradients[level]);
            if (level == 0)
            {
                Assert.NotEmpty(prepared.Gradients[level].EdgePoints);
                Assert.Equal(full.EdgePoints, prepared.Gradients[level].EdgePoints);
            }
            else
            {
                Assert.Empty(prepared.Gradients[level].EdgePoints);
                Assert.True(prepared.Gradients[level].EstimatedBytes < full.EstimatedBytes);
            }
            current = GradientImage.Reduce(current);
        }
    }

    [Theory]
    [InlineData(false, true, 0, false)]
    [InlineData(true, false, 0, false)]
    [InlineData(true, true, 0, true)]
    [InlineData(true, false, 1, true)]
    [InlineData(true, true, 1, true)]
    public void OptionSpecificPreparationPreservesExactMatches(bool verify, bool refine,
        double clutterWeight, bool needsEdges)
    {
        var model = ShapeTrainer.Train(SyntheticScene.Create(96, 96, new MatchPose(48, 48, 0)),
            new(ReadOnlyMemory<byte>.Empty, new(48, 48)), new()
            {
                BuildClutterModel = true
            });
        var image = SyntheticScene.Create(163, 151, new MatchPose(81.3, 75.7, 0.13));
        var full = PreparedSearchImage.Create(image, 8, model.Levels.Length);
        var selected = PreparedSearchImage.CreateForSearch(image, 8, model.Levels.Length, needsEdges,
            default, false, null);
        var options = new SearchOptions
        {
            Verify = verify,
            Refine = refine,
            ClutterWeight = clutterWeight
        };
        var matcher = new ShapeMatcher();

        var reference = matcher.Search(full, model, options);
        var result = matcher.Search(image, model, options);
        var selectedResult = matcher.Search(selected, model, options);

        Assert.NotEmpty(reference.Matches);
        Assert.Equal(reference.Matches.ToArray(), result.Matches.ToArray());
        Assert.Equal(reference.Matches.ToArray(), selectedResult.Matches.ToArray());
        Assert.Equal(reference.Diagnostics.CandidateBudgetExceeded, result.Diagnostics.CandidateBudgetExceeded);
        Assert.Equal(reference.Diagnostics.VerificationBudgetExceeded, result.Diagnostics.VerificationBudgetExceeded);
        Assert.Equal(reference.Diagnostics.MatchLimitExceeded, result.Diagnostics.MatchLimitExceeded);
        if (needsEdges)
        {
            Assert.NotEmpty(selected.Gradients[0].EdgePoints);
            Assert.Equal(full.EstimatedBytes, selected.EstimatedBytes);
        }
        else
        {
            Assert.All(selected.Gradients, gradient => Assert.Empty(gradient.EdgePoints));
            Assert.True(selected.EstimatedBytes < full.EstimatedBytes);
        }
    }

    private static void AssertPlanesEqual(GradientImage expected, GradientImage actual)
    {
        Assert.Equal(expected.X, actual.X);
        Assert.Equal(expected.Y, actual.Y);
        Assert.Equal(expected.Magnitude, actual.Magnitude);
        Assert.Equal(expected.Bits, actual.Bits);
    }
}
