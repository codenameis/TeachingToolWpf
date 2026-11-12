using System.Runtime.CompilerServices;
using VisionAlgorithm.OpenSrc.Common;

namespace VisionAlgorithm.OpenSrc.GoldenVariation;

/// <summary>
/// Training specification for building a statistical <see cref="VariationModel"/> from normal reference samples.
/// </summary>
public readonly record struct VariationTrainingSpec
{
    public const float DefaultAbsoluteMinimumThreshold = 14f;
    public const float DefaultSigmaMultiplier = 3.5f;
    public const float DefaultEdgeJitterTolerancePixels = 0.65f;

    private readonly bool _isInitialized;

    public float AbsoluteMinimumThreshold { get; }
    public float SigmaMultiplier { get; }
    public float EdgeJitterTolerancePixels { get; }
    public bool IsValid => _isInitialized && AbsoluteMinimumThreshold > 0f && SigmaMultiplier > 0f;

    public VariationTrainingSpec(
        float absoluteMinimumThreshold = DefaultAbsoluteMinimumThreshold,
        float sigmaMultiplier = DefaultSigmaMultiplier,
        float edgeJitterTolerancePixels = DefaultEdgeJitterTolerancePixels)
    {
        if (!float.IsFinite(absoluteMinimumThreshold) || absoluteMinimumThreshold <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(absoluteMinimumThreshold), "AbsoluteMinimumThreshold must be positive.");
        }

        if (!float.IsFinite(sigmaMultiplier) || sigmaMultiplier <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(sigmaMultiplier), "SigmaMultiplier must be positive.");
        }

        if (!float.IsFinite(edgeJitterTolerancePixels) || edgeJitterTolerancePixels < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(edgeJitterTolerancePixels), "EdgeJitterTolerancePixels must be non-negative.");
        }

        AbsoluteMinimumThreshold = absoluteMinimumThreshold;
        SigmaMultiplier = sigmaMultiplier;
        EdgeJitterTolerancePixels = edgeJitterTolerancePixels;
        _isInitialized = true;
    }

    public static VariationTrainingSpec Default => new(
        DefaultAbsoluteMinimumThreshold,
        DefaultSigmaMultiplier,
        DefaultEdgeJitterTolerancePixels);

    public void EnsureValid()
    {
        if (!IsValid)
        {
            throw new InvalidOperationException("VariationTrainingSpec was not initialized via its constructor.");
        }
    }
}
