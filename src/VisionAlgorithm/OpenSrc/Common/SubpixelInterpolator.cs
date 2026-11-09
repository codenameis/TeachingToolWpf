// File: src/VisionAlgorithm/OpenSrc/Common/SubpixelInterpolator.cs
using System.Runtime.CompilerServices;

namespace VisionAlgorithm.OpenSrc.Common;

/// <summary>
/// Shared sub-pixel interpolation and continuous sampling kernels used across caliper metrology, edge extraction, and ridge detection.
/// </summary>
public static class SubpixelInterpolator
{
    // [Math Why]: Guards parabolic vertex denominator (2 * (fMinus - 2 * fZero + fPlus)) against flat/collinear division by zero.
    private const float ParabolicDenominatorEpsilon = 1e-6f;

    // [Math Why]: Valid sub-pixel extremum offset around a discrete local peak must lie within [-0.5, +0.5] pixels.
    private const float MaximumSubpixelOffset = 0.5f;

    // [Math Why]: Vertex value of parabola y(x) = a*x^2 + b*x + c at x* = -b/(2a) is f(0) - 0.25 * (f(-1) - f(+1)) * delta.
    private const float ParabolicPeakQuarterFactor = 0.25f;

    /// <summary>
    /// Computes the sub-pixel vertex offset $\delta \in [-0.5, 0.5]$ of a 1D parabola fitted through
    /// three equidistant samples $(f_{-1}, f_0, f_{+1})$:
    /// $\delta = \frac{f_{-1} - f_{+1}}{2(f_{-1} - 2f_0 + f_{+1})}$.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float ComputeParabolicPeakOffset(float fMinus, float fZero, float fPlus)
    {
        float denom = 2f * (fMinus - (2f * fZero) + fPlus);
        if (MathF.Abs(denom) <= ParabolicDenominatorEpsilon)
        {
            return 0f;
        }

        float delta = (fMinus - fPlus) / denom;
        return Math.Clamp(delta, -MaximumSubpixelOffset, MaximumSubpixelOffset);
    }

    /// <summary>
    /// Computes both the sub-pixel vertex offset $\delta \in [-0.5, 0.5]$ and the interpolated vertex amplitude
    /// $f(\delta) = f_0 - \frac{1}{4}(f_{-1} - f_{+1})\delta$ of a 1D parabola fitted through $(f_{-1}, f_0, f_{+1})$.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float InterpolateParabolicPeak(float fMinus, float fZero, float fPlus, out float peakValue)
    {
        float delta = ComputeParabolicPeakOffset(fMinus, fZero, fPlus);
        peakValue = fZero - (ParabolicPeakQuarterFactor * (fMinus - fPlus) * delta);
        return delta;
    }

    /// <summary>
    /// Computes both the sub-pixel vertex offset $\delta \in [-0.5, 0.5]$ and the interpolated vertex amplitude
    /// of a 1D Gaussian peak fitted through positive samples $(f_{-1}, f_0, f_{+1})$.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float InterpolateGaussianPeak(float fMinus, float fZero, float fPlus, out float peakValue)
    {
        float lm = MathF.Log(MathF.Max(ParabolicDenominatorEpsilon, fMinus));
        float l0 = MathF.Log(MathF.Max(ParabolicDenominatorEpsilon, fZero));
        float lp = MathF.Log(MathF.Max(ParabolicDenominatorEpsilon, fPlus));
        float delta = ComputeParabolicPeakOffset(lm, l0, lp);
        float logPeak = l0 - (ParabolicPeakQuarterFactor * (lm - lp) * delta);
        peakValue = MathF.Exp(logPeak);
        return delta;
    }

    /// <summary>
    /// Samples an 8-bit <see cref="ReadOnlyImageView2D{T}"/> at continuous image coordinates $(worldX, worldY)$ using bilinear interpolation.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float SampleBilinear(
        ReadOnlyImageView2D<byte> imageView,
        float worldX,
        float worldY)
    {
        imageView.EnsureValid();
        return SampleBilinearPixelCenter(imageView.Pixels, imageView.Width, imageView.Height, imageView.Stride, worldX, worldY);
    }

    /// <summary>
    /// Samples an 8-bit grayscale image at continuous image coordinates $(worldX, worldY)$ using exact pixel-center
    /// convention (where pixel $(c, r)$ has its optical center at $(c + 0.5, r + 0.5)$) and bilinear interpolation.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float SampleBilinearPixelCenter(
        ReadOnlySpan<byte> image,
        int width,
        int height,
        int stride,
        float worldX,
        float worldY)
    {
        float sx = Math.Clamp(worldX - 0.5f, 0f, width - 1);
        float sy = Math.Clamp(worldY - 0.5f, 0f, height - 1);

        int x0 = (int)MathF.Floor(sx);
        int y0 = (int)MathF.Floor(sy);
        float fx = sx - x0;
        float fy = sy - y0;

        int x1 = Math.Min(x0 + 1, width - 1);
        int y1 = Math.Min(y0 + 1, height - 1);

        int r0 = y0 * stride;
        int r1 = y1 * stride;

        float p00 = image[r0 + x0];
        float p10 = image[r0 + x1];
        float p01 = image[r1 + x0];
        float p11 = image[r1 + x1];

        float top = p00 + ((p10 - p00) * fx);
        float bottom = p01 + ((p11 - p01) * fx);
        return top + ((bottom - top) * fy);
    }
}
