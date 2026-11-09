// File: src/VisionAlgorithm/OpenSrc/EdgeFewShot/Math/SimpleShotNormalizer.cs
using System.Runtime.CompilerServices;
using CommonSimpleShot = VisionAlgorithm.OpenSrc.Common.SimpleShotNormalizer;

namespace VisionAlgorithm.OpenSrc.EdgeFewShot.Math;

/// <summary>
/// SimpleShot (Wang et al., 2019) feature centering and L2 unit-hypersphere normalization:
/// <c>f_tilde = (f - f_bar_global) / max(||f - f_bar_global||_2, epsilon)</c>.
/// Delegates all core SIMD vector operations to <see cref="CommonSimpleShot"/> as the single source of truth.
/// </summary>
public sealed class SimpleShotNormalizer
{
    // [Math Why]: Floor on squared L2 norm prevents NaN/Inf when a feature vector coincides with the global mean.
    public const float MinNormEpsilon = CommonSimpleShot.MinSquaredNormEpsilon;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ComputeGlobalMean(
        ReadOnlySpan<float> flattenedFeatures,
        int sampleCount,
        int featureDimension,
        Span<float> globalMeanOut)
        => CommonSimpleShot.ComputeGlobalMean(flattenedFeatures, sampleCount, featureDimension, globalMeanOut);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CenterAndNormalizeInto(
        ReadOnlySpan<float> featureVector,
        ReadOnlySpan<float> globalMean,
        Span<float> normalizedOut)
        => CommonSimpleShot.CenterAndL2Normalize(featureVector, globalMean, normalizedOut);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void NormalizeInPlace(Span<float> vector)
        => CommonSimpleShot.L2NormalizeInPlace(vector);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static float ComputeSquaredL2Norm(ReadOnlySpan<float> vector)
        => CommonSimpleShot.ComputeSquaredL2Norm(vector);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void AccumulateVectorInto(ReadOnlySpan<float> source, Span<float> accumulator)
        => CommonSimpleShot.AccumulateVectorInto(source, accumulator);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void ScaleInPlace(Span<float> vector, float scale)
        => CommonSimpleShot.ScaleInPlace(vector, scale);
}
