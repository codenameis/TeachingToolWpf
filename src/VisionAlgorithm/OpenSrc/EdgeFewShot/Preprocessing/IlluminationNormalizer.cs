// File: src/VisionAlgorithm/OpenSrc/EdgeFewShot/Preprocessing/IlluminationNormalizer.cs
using System.Runtime.CompilerServices;
using VisionAlgorithm.OpenSrc.Preprocessing;

namespace VisionAlgorithm.OpenSrc.EdgeFewShot.Preprocessing;

/// <summary>
/// Local Z-score illumination normalizer computing <c>I_hat(x,y) = (I(x,y) - mu) / max(sigma, 1e-4)</c>
/// by delegating to the unified <see cref="ContrastAndAdaptiveFilterEngine"/> in <c>OpenSrc/Preprocessing</c>.
/// </summary>
public sealed class IlluminationNormalizer
{
    // [Math Why]: Floor on standard deviation prevents division-by-zero explosion on flat/uniform background patches.
    public const float MinStandardDeviation = ContrastAndAdaptiveFilterEngine.MinStandardDeviation;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void NormalizeInPlace(Span<float> roiPixels)
    {
        if (roiPixels.IsEmpty)
        {
            throw new ArgumentException("ROI pixel span must not be empty.", nameof(roiPixels));
        }

        ContrastAndAdaptiveFilterEngine.NormalizeZScore(roiPixels, roiPixels);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void NormalizeInto(ReadOnlySpan<float> sourceRoi, Span<float> destinationRoi)
    {
        if (sourceRoi.IsEmpty)
        {
            throw new ArgumentException("Source ROI pixel span must not be empty.", nameof(sourceRoi));
        }

        if (destinationRoi.Length < sourceRoi.Length)
        {
            throw new ArgumentException("Destination ROI span must be at least as long as source ROI span.", nameof(destinationRoi));
        }

        ContrastAndAdaptiveFilterEngine.NormalizeZScore(sourceRoi, destinationRoi.Slice(0, sourceRoi.Length));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static (float Mean, float StdDev) ComputeMeanAndStdDev(ReadOnlySpan<float> values)
    {
        return ContrastAndAdaptiveFilterEngine.ComputeMeanAndStdDev(values);
    }
}
