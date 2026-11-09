// File: src/VisionAlgorithm/OpenSrc/Common/RobustMestimator.cs
using System.Runtime.CompilerServices;

namespace VisionAlgorithm.OpenSrc.Common;

/// <summary>
/// Shared robust M-estimator weighting and Median Absolute Deviation (MAD) scale estimation kernels
/// for Iteratively Reweighted Least Squares (IRLS) line/pose fitting.
/// </summary>
public static class RobustMestimator
{
    // [Math Why]: Tukey's biweight tuning constant c = 4.6851 achieves 95% asymptotic efficiency on Gaussian noise.
    public const float TukeyBiweightConstant = 4.6851f;

    // [Math Why]: Conversion factor 1 / Phi^-1(0.75) = 1 / 0.67449 = 1.4826 scales MAD to equivalent Gaussian standard deviation.
    public const float MadToGaussianSigmaScale = 1.4826f;

    // [Math Why]: Minimum robust scale floor prevents division by zero when residuals are collinear to machine precision.
    public const float MinimumScaleFloor = 1e-3f;

    /// <summary>
    /// Evaluates Tukey's Biweight M-estimator weight $w(r) = (1 - (r / (c\hat{\sigma}))^2)^2$ for $|r| &lt; c\hat{\sigma}$, else $0$.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float ComputeTukeyBiweight(float absResidual, float cutoffScale)
    {
        if (absResidual >= cutoffScale || cutoffScale <= 0f)
        {
            return 0f;
        }

        float u = absResidual / cutoffScale;
        float oneMinusU2 = 1f - (u * u);
        return oneMinusU2 * oneMinusU2;
    }

    /// <summary>
    /// Computes the robust Gaussian-equivalent scale $\hat{\sigma} = \max(\text{floor}, 1.4826 \cdot \operatorname{median}_i |r_i|)$
    /// by sorting the absolute residuals in <paramref name="absResidualsScratch"/> in-place.
    /// </summary>
    public static float EstimateMadSigmaInPlace(Span<float> absResidualsScratch, float minimumFloor = MinimumScaleFloor)
    {
        if (absResidualsScratch.IsEmpty)
        {
            return minimumFloor;
        }

        absResidualsScratch.Sort();
        int n = absResidualsScratch.Length;
        int mid = n >> 1;
        float median = (n & 1) == 0
            ? 0.5f * (absResidualsScratch[mid - 1] + absResidualsScratch[mid])
            : absResidualsScratch[mid];
        return MathF.Max(minimumFloor, median * MadToGaussianSigmaScale);
    }
}
