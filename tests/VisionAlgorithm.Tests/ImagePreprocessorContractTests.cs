using VisionAlgorithm.OpenSrc.PatternMatching;

namespace VisionAlgorithm.Tests;

public sealed class ImagePreprocessorContractTests
{
    [Fact]
    public void SharedNormalizationTakesOwnershipAndKeepsSubthresholdMagnitude()
    {
        float[] x = [3, 0, 1];
        float[] y = [4, -5, 0];
        var result = GradientPlanes.FromSobelDerivatives(x, y, 3);
        Assert.Same(x, result.X);
        Assert.Same(y, result.Y);
        Assert.Equal(new float[] { 5, 5, 1 }, result.Magnitude);
        Assert.Equal(new float[] { .6f, 0, 0 }, result.X);
        Assert.Equal(new float[] { .8f, -1, 0 }, result.Y);
        Assert.Equal(new byte[] { 4, 16, 0 }, result.Bits);
    }

    [Fact]
    public void SharedNormalizationRejectsMismatchedLengthsAndPreCancellation()
    {
        Assert.Throws<ArgumentException>(() => GradientPlanes.FromSobelDerivatives([1], [1, 2], 3));
        float[] x = [3];
        float[] y = [4];
        Assert.Throws<OperationCanceledException>(() => GradientPlanes.FromSobelDerivatives(x, y, 3, new(true)));
        Assert.Equal(3, x[0]);
        Assert.Equal(4, y[0]);
    }

    [Fact]
    public void PreparedImageUsesInjectedPreprocessorForEveryLevel()
    {
        using var preprocessor = new RecordingPreprocessor();
        var prepared = PreparedSearchImage.Create(new(new byte[64 * 48], 64, 48, 64), 3, 3,
            preprocessor: preprocessor);

        Assert.Equal(new[] { (64, 48), (32, 24), (16, 12) }, preprocessor.Sizes);
        Assert.Equal(3, prepared.LevelCount);
        Assert.All(prepared.Gradients, gradient => Assert.Empty(gradient.EdgePoints));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void InvalidBackendOutputIsRejectedBeforeGradientAccess(int plane)
    {
        using var preprocessor = new RecordingPreprocessor { InvalidPlane = plane };
        Assert.Throws<InvalidOperationException>(() => PreparedSearchImage.Create(
            new(new byte[64], 8, 8, 8), 3, 1, preprocessor: preprocessor));
    }

    [Fact]
    public void BackendFailureDoesNotFallBackToCpu()
    {
        using var preprocessor = new RecordingPreprocessor { Failure = true };
        var error = Assert.Throws<InvalidOperationException>(() => PreparedSearchImage.Create(
            new(new byte[64], 8, 8, 8), 3, 1, preprocessor: preprocessor));
        Assert.Equal("backend failed", error.Message);
    }

    [Fact]
    public void CanceledPreparationDoesNotInvokeBackend()
    {
        using var preprocessor = new RecordingPreprocessor();
        Assert.Throws<OperationCanceledException>(() => PreparedSearchImage.Create(
            new(new byte[64], 8, 8, 8), 3, 1, new(true), preprocessor: preprocessor));
        Assert.Empty(preprocessor.Sizes);
    }

    [Fact]
    public void InputBudgetIsValidatedBeforeBackendInvocation()
    {
        using var preprocessor = new RecordingPreprocessor();
        Assert.Throws<ArgumentException>(() => PreparedSearchImage.Create(
            new(new byte[64], 8, 8, 8), 3, 1, maximumPixels: 63, preprocessor: preprocessor));
        Assert.Empty(preprocessor.Sizes);
    }

    private sealed class RecordingPreprocessor : IImagePreprocessor
    {
        public List<(int Width, int Height)> Sizes { get; } = [];
        public int InvalidPlane { get; init; } = -1;
        public bool Failure
        {
            get; init;
        }
        public ImagePreprocessingDiagnostics Diagnostics => new("Test", "Test", Sizes.Count, 0, 0);

        public GradientPlanes Prepare(GrayImage image, float minimumGradient, CancellationToken cancellationToken = default)
        {
            Sizes.Add((image.Width, image.Height));
            if (Failure)
            {
                throw new InvalidOperationException("backend failed");
            }
            var count = image.Width * image.Height;
            return new(new float[InvalidPlane == 0 ? 0 : count], new float[InvalidPlane == 1 ? 0 : count],
                new float[InvalidPlane == 2 ? 0 : count], new byte[InvalidPlane == 3 ? 0 : count]);
        }

        public void Dispose()
        {
        }
    }
}
