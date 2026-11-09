// File: src/VisionAlgorithm/OpenSrc/GoldenVariation/VariationModel.cs
namespace VisionAlgorithm.OpenSrc.GoldenVariation;

/// <summary>
/// Holds the learned per-pixel statistical distribution ($\mu(x, y)$, $\sigma(x, y)$) and gradient-based
/// subpixel alignment tolerance band for statistical golden-template difference inspection.
/// </summary>
public sealed class VariationModel
{
    private readonly float[] _meanImage;
    private readonly float[] _stdDevImage;
    private readonly float[] _edgeGradientImage;
    private readonly float[] _adaptiveThresholdImage;

    public int Width { get; }
    public int Height { get; }
    public int TrainedSampleCount { get; }
    public float AbsoluteMinimumThreshold { get; }
    public float SigmaMultiplier { get; }
    public float EdgeJitterTolerancePixels { get; }

    public ReadOnlySpan<float> MeanImage => _meanImage;
    public ReadOnlySpan<float> StdDevImage => _stdDevImage;
    public ReadOnlySpan<float> EdgeGradientImage => _edgeGradientImage;
    public ReadOnlySpan<float> AdaptiveThresholdImage => _adaptiveThresholdImage;

    public VariationModel(
        int width,
        int height,
        int trainedSampleCount,
        float absoluteMinimumThreshold,
        float sigmaMultiplier,
        float edgeJitterTolerancePixels,
        float[] meanImage,
        float[] stdDevImage,
        float[] edgeGradientImage,
        float[] adaptiveThresholdImage)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(width, 2);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(height, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(trainedSampleCount, 1);
        ArgumentNullException.ThrowIfNull(meanImage);
        ArgumentNullException.ThrowIfNull(stdDevImage);
        ArgumentNullException.ThrowIfNull(edgeGradientImage);
        ArgumentNullException.ThrowIfNull(adaptiveThresholdImage);

        int expectedLength = checked(width * height);
        if (meanImage.Length < expectedLength ||
            stdDevImage.Length < expectedLength ||
            edgeGradientImage.Length < expectedLength ||
            adaptiveThresholdImage.Length < expectedLength)
        {
            throw new ArgumentException("All model buffers must have length >= width * height.");
        }

        Width = width;
        Height = height;
        TrainedSampleCount = trainedSampleCount;
        AbsoluteMinimumThreshold = absoluteMinimumThreshold;
        SigmaMultiplier = sigmaMultiplier;
        EdgeJitterTolerancePixels = edgeJitterTolerancePixels;

        _meanImage = meanImage;
        _stdDevImage = stdDevImage;
        _edgeGradientImage = edgeGradientImage;
        _adaptiveThresholdImage = adaptiveThresholdImage;
    }
}
