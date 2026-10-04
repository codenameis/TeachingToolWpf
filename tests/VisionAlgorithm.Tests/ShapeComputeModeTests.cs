using VisionAlgorithm.OpenSrc.PatternMatching;

namespace VisionAlgorithm.Tests;

public sealed class ShapeComputeModeTests
{
    [Fact]
    public void GpuModeRequiresInjectedBackendForRawSearchAndRefinement()
    {
        var model = MatchingTests.Train();
        var image = SyntheticScene.Create(96, 96, new MatchPose(48, 48, 0));
        var matcher = new ShapeMatcher();
        var options = new SearchOptions { ComputeMode = InspectionComputeMode.GpuPreprocessing };

        Assert.Throws<InvalidOperationException>(() => matcher.Search(image, model, options));
        Assert.Throws<InvalidOperationException>(() => matcher.RefineCandidate(image, model, new(48, 48, 0), 1, options));
    }

    [Fact]
    public void PreparedSearchDoesNotRequireAnotherBackend()
    {
        var model = MatchingTests.Train();
        var image = SyntheticScene.Create(96, 96, new MatchPose(48, 48, 0));
        var prepared = PreparedSearchImage.Create(image, model.Options.MinimumGradient, model.Levels.Length);
        var result = new ShapeMatcher().Search(prepared, model, new()
        {
            ComputeMode = InspectionComputeMode.GpuPreprocessing
        });

        Assert.Single(result.Matches);
    }

    [Fact]
    public void CpuModeDoesNotUseOrDisposeInjectedBackend()
    {
        using var preprocessor = new FailingPreprocessor();
        var model = MatchingTests.Train();
        var image = SyntheticScene.Create(96, 96, new MatchPose(48, 48, 0));
        var matcher = new ShapeMatcher(preprocessor);

        Assert.Single(matcher.Search(image, model, new()).Matches);
        Assert.Single(matcher.RefineCandidate(image, model, new(48, 48, 0), 1, new()).Matches);
        Assert.Equal(0, preprocessor.Calls);
        Assert.False(preprocessor.Disposed);
    }

    [Fact]
    public void GpuFailureIsPropagatedWithoutFallbackOrTakingOwnership()
    {
        using var preprocessor = new FailingPreprocessor();
        var model = MatchingTests.Train();
        var image = SyntheticScene.Create(96, 96, new MatchPose(48, 48, 0));
        var matcher = new ShapeMatcher(preprocessor);
        var options = new SearchOptions { ComputeMode = InspectionComputeMode.GpuPreprocessing };

        Assert.Equal("GPU test failure", Assert.Throws<InvalidOperationException>(() => matcher.Search(image, model, options)).Message);
        Assert.Equal("GPU test failure", Assert.Throws<InvalidOperationException>(() => matcher.RefineCandidate(
            image, model, new(48, 48, 0), 1, options)).Message);
        Assert.Equal(2, preprocessor.Calls);
        Assert.False(preprocessor.Disposed);
    }

    [Fact]
    public void UndefinedComputeModeIsRejected()
        => Assert.Throws<ArgumentOutOfRangeException>(() => (new SearchOptions { ComputeMode = (InspectionComputeMode)2 }).Validate());

    private sealed class FailingPreprocessor : IImagePreprocessor
    {
        public int Calls
        {
            get; private set;
        }
        public bool Disposed
        {
            get; private set;
        }
        public ImagePreprocessingDiagnostics Diagnostics => new("Test", "Test", Calls, 0, 0);

        public GradientPlanes Prepare(GrayImage image, float minimumGradient, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("GPU test failure");
        }

        public void Dispose() => Disposed = true;
    }
}
