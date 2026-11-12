using System.Runtime.CompilerServices;

namespace VisionKernels;

/// <summary>
/// 3x3 Sobel gradient operators.
/// <para>
/// Note on scaling conventions across engines:
/// <list type="bullet">
/// <item><description><see cref="ComputeNormalizedGray8Gradients"/> divides the 3x3 Sobel sum <c>[-1, 0, 1; -2, 0, 2; -1, 0, 1]</c> by <c>8.0</c>, yielding step-edge amplitude in <c>[0, 255]</c> (used by <c>CpuMatching</c> and <c>StegerShape</c>).</description></item>
/// <item><description><c>PatMax</c> (<c>SubPixelEdgeExtractor</c>) uses the rotation-invariant 3x3 Scharr operator <c>[-3, 0, 3; -10, 0, 10; -3, 0, 3] / 32</c> on 5x5 binomial-smoothed float rows and therefore intentionally retains its own SIMD Scharr pipeline.</description></item>
/// </list>
/// </para>
/// </summary>
public static class SobelKernel
{
    private const double NormalizationDivisor = 8.0;

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static (double X, double Y)[] ComputeNormalizedGray8Gradients(ReadOnlySpan<byte> pixels, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 3);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 3);
        if (pixels.Length < checked(width * height))
        {
            throw new ArgumentException("Pixel buffer is smaller than width * height.", nameof(pixels));
        }

        var result = new (double X, double Y)[width * height];
        for (var y = 1; y < height - 1; y++)
        {
            var prevRow = (y - 1) * width;
            var currRow = y * width;
            var nextRow = (y + 1) * width;
            for (var x = 1; x < width - 1; x++)
            {
                var gx = (pixels[prevRow + x + 1] + (2 * pixels[currRow + x + 1]) + pixels[nextRow + x + 1] -
                    pixels[prevRow + x - 1] - (2 * pixels[currRow + x - 1]) - pixels[nextRow + x - 1]) / NormalizationDivisor;
                var gy = (pixels[nextRow + x - 1] + (2 * pixels[nextRow + x]) + pixels[nextRow + x + 1] -
                    pixels[prevRow + x - 1] - (2 * pixels[prevRow + x]) - pixels[prevRow + x + 1]) / NormalizationDivisor;
                result[currRow + x] = (gx, gy);
            }
        }

        return result;
    }
}
