using System.Runtime.CompilerServices;

namespace VisionKernels;

/// <summary>
/// Analytical 1st-derivative 1D Gaussian convolution kernel shared across subpixel caliper tools.
/// </summary>
public static class GaussianDerivative1DKernel
{
    /// <summary>
    /// Convolves <paramref name="profile"/> with a step-amplitude-normalized 1D Gaussian derivative filter of radius <paramref name="radius"/> and standard deviation <paramref name="sigma"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void ConvolveNormalized(
        ReadOnlySpan<float> profile,
        float sigma,
        int radius,
        Span<float> kernelScratch,
        Span<float> responseOut)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sigma);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(radius);

        var kernelLength = (2 * radius) + 1;
        if (kernelScratch.Length < kernelLength)
        {
            throw new ArgumentException("Kernel scratch buffer is smaller than (2 * radius) + 1.", nameof(kernelScratch));
        }

        if (responseOut.Length < profile.Length)
        {
            throw new ArgumentException("Response buffer is smaller than profile length.", nameof(responseOut));
        }

        var invTwoSigmaSq = 1.0f / (2.0f * sigma * sigma);
        var positiveIntegral = 0.0f;

        // [Math Why]: Analytical 1st derivative of Gaussian g'(x) = -x / sigma^2 * exp(-x^2 / (2*sigma^2)).
        // Normalizing the positive half-weights to sum to 1.0 preserves exact unit step-edge amplitude (I_right - I_left).
        for (var m = -radius; m <= radius; m++)
        {
            var w = m * MathF.Exp(-(m * m) * invTwoSigmaSq);
            kernelScratch[m + radius] = w;
            if (m > 0)
            {
                positiveIntegral += w;
            }
        }

        var normFactor = positiveIntegral > 0.0f ? 1.0f / positiveIntegral : 1.0f;
        for (var k = 0; k < kernelLength; k++)
        {
            kernelScratch[k] *= normFactor;
        }

        var n = profile.Length;
        responseOut.Slice(0, n).Clear();
        for (var u = radius; u < n - radius; u++)
        {
            var acc = 0.0f;
            for (var m = -radius; m <= radius; m++)
            {
                acc += profile[u + m] * kernelScratch[m + radius];
            }

            responseOut[u] = acc;
        }
    }
}
