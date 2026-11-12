using System.Numerics;
using System.Runtime.CompilerServices;
using VisionAlgorithm.OpenSrc.Common;

namespace VisionAlgorithm.OpenSrc.Curvilinear;

/// <summary>
/// Implements Carsten Steger's 1998 curvilinear structure detector ("An Unbiased Detector of Curvilinear Structures",
/// IEEE TPAMI 20(2):113-125) using zero-allocation 1D separable Gaussian convolution (<c>Reflect101</c> boundary handling)
/// + central finite-difference Hessian ($r_x, r_y, r_{xx}, r_{xy}, r_{yy}$)
/// and 2nd-order Taylor subpixel zero-crossing $(\tilde{p}_x, \tilde{p}_y) = (t n_x, t n_y) \in [-0.5, 0.5]^2$.
/// </summary>
public static class HessianRidgeDetector
{
    // [Math Why]: Steger (1998) Eq. (13): sigma >= w / sqrt(3) ensures the 2nd derivative of a Gaussian convolved
    // with a bar profile of width w has a single well-defined extremum at the centerline.
    private const float InvSqrt3 = 0.577350269f;

    // [Math Why]: Minimum optimal scale (0.8 px) prevents aliasing on sub-2px lines.
    private const float MinOptimalSigma = 0.8f;

    // [Math Why]: Minimum allowed Gaussian sigma (0.6 px) ensures well-conditioned discrete Gaussian sampling.
    private const float MinGaussianSigma = 0.6f;

    // [Math Why]: Minimum 1D Gaussian kernel radius of 2 pixels (5-tap filter).
    private const int MinKernelRadius = 2;

    // [Math Why]: Truncating the 1D Gaussian kernel at 3.5 sigma captures >99.95% of filter energy.
    private const float KernelRadiusMultiplier = 3.5f;

    // [Math Why]: Steger (1998) pixel cell inclusion criterion requires (t * nx, t * ny) in [-0.5, +0.5] x [-0.5, +0.5].
    private const float SubpixelCellBoundary = 0.5f;

    // [Math Why]: Minimum curvature denominator prevents zero division on flat regions.
    private const float CurvatureEpsilon = 1e-6f;

    // [Math Why]: Central 2nd-derivative cross-term r_xy = (I(x+1,y+1) - I(x-1,y+1) - I(x+1,y-1) + I(x-1,y-1)) / 4.
    private const float CrossDerivativeScale = 0.25f;

    // [Math Why]: Minimum image dimension required for 3x3 Hessian finite differences inside Gaussian margin.
    private const int MinImageDimension = 4;

    // [Perf Why]: Pre-sizing centerline point list avoids repeated dynamic array doubling on typical ROI dimensions.
    private const int MinInitialPointCapacity = 32;

    /// <summary>
    /// Computes the optimal Steger Gaussian scale $\sigma = \max(0.8, \text{expectedLineWidth} / \sqrt{3})$.
    /// </summary>
    public static float ComputeOptimalSigma(float expectedLineWidthPixels)
        => MathF.Max(MinOptimalSigma, expectedLineWidthPixels * InvSqrt3);

    /// <summary>
    /// Extracts subpixel centerline points from a rectified floating-point ROI patch.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static List<RidgeCenterlinePoint> ExtractCenterlinePoints(
        ReadOnlySpan<float> image,
        int width,
        int height,
        float gaussianSigma,
        float minSecondDerivativeStrength,
        RidgePolarity polarity,
        InspectionWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(width, MinImageDimension);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(height, MinImageDimension);

        int pixelCount = checked(width * height);
        if (image.Length < pixelCount)
        {
            throw new ArgumentException("Image buffer is smaller than width * height.", nameof(image));
        }

        float sigma = MathF.Max(MinGaussianSigma, gaussianSigma);
        int radius = Math.Max(MinKernelRadius, (int)MathF.Ceiling(sigma * KernelRadiusMultiplier));
        int kernelLen = (2 * radius) + 1;

        Span<float> kernel = workspace.GetFloatBufferB(kernelLen);
        BuildNormalizedGaussianKernel1D(sigma, radius, kernel);

        Span<float> tempHorizontal = workspace.GetFloatBufferC(pixelCount);
        Span<float> smoothed = workspace.GetFloatBufferD(pixelCount);

        ConvolveHorizontalReflect101(image, tempHorizontal, width, height, kernel, radius);
        ConvolveVerticalReflect101(tempHorizontal, smoothed, width, height, kernel, radius);

        List<RidgeCenterlinePoint> points = new(Math.Max(MinInitialPointCapacity, Math.Max(width, height) * 2));
        float sigmaSq = sigma * sigma;
        int margin = radius + 1;

        for (int y = margin; y < height - margin; y++)
        {
            int rowPrev = (y - 1) * width;
            int rowCurr = y * width;
            int rowNext = (y + 1) * width;

            for (int x = margin; x < width - margin; x++)
            {
                float c = smoothed[rowCurr + x];
                float left = smoothed[rowCurr + x - 1];
                float right = smoothed[rowCurr + x + 1];
                float up = smoothed[rowPrev + x];
                float down = smoothed[rowNext + x];

                // Scale-normalized 1st and 2nd partial derivatives
                float rx = 0.5f * (right - left);
                float ry = 0.5f * (down - up);
                float rxxRaw = right - (2f * c) + left;
                float ryyRaw = down - (2f * c) + up;
                float rxyRaw = CrossDerivativeScale * ((smoothed[rowNext + x + 1] - smoothed[rowNext + x - 1]) -
                                                       (smoothed[rowPrev + x + 1] - smoothed[rowPrev + x - 1]));
                float rxx = rxxRaw * sigmaSq;
                float ryy = ryyRaw * sigmaSq;
                float rxy = rxyRaw * sigmaSq;

                // Eigen-decomposition of 2x2 symmetric Hessian [rxx, rxy; rxy, ryy]
                SolveSymmetric2x2Eigen(rxx, rxy, ryy, out float evalMaxAbs, out float nx, out float ny);

                // For a bright ridge, 2nd derivative across the ridge is strongly negative (eval < 0).
                // For a dark crack/valley, 2nd derivative across the crack is strongly positive (eval > 0).
                bool matchesPolarity = polarity == RidgePolarity.BrightLine
                    ? evalMaxAbs < -minSecondDerivativeStrength
                    : evalMaxAbs > minSecondDerivativeStrength;

                if (!matchesPolarity)
                {
                    continue;
                }

                // [Math Why]: Steger (1998) Eq. (5): Directional 1st and 2nd derivatives along unit eigenvector (nx, ny):
                // t = -(rx * nx + ry * ny) / (rxx_raw * nx^2 + 2 * rxy_raw * nx * ny + ryy_raw * ny^2).
                float unnormalizedSecondDeriv = (rxxRaw * nx * nx) + (2f * rxyRaw * nx * ny) + (ryyRaw * ny * ny);
                if (MathF.Abs(unnormalizedSecondDeriv) < CurvatureEpsilon)
                {
                    continue;
                }

                float t = -((rx * nx) + (ry * ny)) / unnormalizedSecondDeriv;
                float px = t * nx;
                float py = t * ny;

                if (MathF.Abs(px) <= SubpixelCellBoundary && MathF.Abs(py) <= SubpixelCellBoundary)
                {
                    // Pixel center convention: grid pixel (x, y) has center (x + 0.5, y + 0.5)
                    Vector2 subpixelPos = new(x + 0.5f + px, y + 0.5f + py);
                    points.Add(new RidgeCenterlinePoint(
                        GridX: x,
                        GridY: y,
                        SubpixelPosition: subpixelPos,
                        NormalDirection: new Vector2(nx, ny),
                        SecondDerivativeStrength: MathF.Abs(evalMaxAbs)));
                }
            }
        }

        return points;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SolveSymmetric2x2Eigen(
        float rxxScaled,
        float rxyScaled,
        float ryyScaled,
        out float maxAbsEigenvalue,
        out float nx,
        out float ny)
    {
        float diff = rxxScaled - ryyScaled;
        float hypot = MathF.Sqrt((diff * diff) + (4f * rxyScaled * rxyScaled));
        float lambda1 = 0.5f * (rxxScaled + ryyScaled + hypot);
        float lambda2 = 0.5f * (rxxScaled + ryyScaled - hypot);

        maxAbsEigenvalue = MathF.Abs(lambda1) >= MathF.Abs(lambda2) ? lambda1 : lambda2;
        float theta = 0.5f * MathF.Atan2(2f * rxyScaled, diff);
        (float sinT, float cosT) = MathF.SinCos(theta);

        if (MathF.Abs(lambda1) >= MathF.Abs(lambda2))
        {
            nx = cosT;
            ny = sinT;
        }
        else
        {
            nx = -sinT;
            ny = cosT;
        }
    }

    private static void BuildNormalizedGaussianKernel1D(float sigma, int radius, Span<float> kernel)
    {
        float invTwoSigmaSq = 1f / (2f * sigma * sigma);
        float sum = 0f;
        for (int k = -radius; k <= radius; k++)
        {
            float w = MathF.Exp(-(k * k) * invTwoSigmaSq);
            kernel[k + radius] = w;
            sum += w;
        }

        float invSum = 1f / sum;
        for (int i = 0; i < kernel.Length; i++)
        {
            kernel[i] *= invSum;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void ConvolveHorizontalReflect101(
        ReadOnlySpan<float> src,
        Span<float> dst,
        int width,
        int height,
        ReadOnlySpan<float> kernel,
        int radius)
    {
        int leftBorderEnd = Math.Min(width, radius);
        int rightBorderStart = Math.Max(leftBorderEnd, width - radius);

        for (int y = 0; y < height; y++)
        {
            int rowOffset = y * width;
            ReadOnlySpan<float> srcRow = src.Slice(rowOffset, width);
            Span<float> dstRow = dst.Slice(rowOffset, width);

            for (int x = 0; x < leftBorderEnd; x++)
            {
                float acc = 0f;
                for (int k = -radius; k <= radius; k++)
                {
                    acc += srcRow[Reflect101Index(x + k, width)] * kernel[k + radius];
                }

                dstRow[x] = acc;
            }

            for (int x = leftBorderEnd; x < rightBorderStart; x++)
            {
                float acc = 0f;
                for (int k = -radius; k <= radius; k++)
                {
                    acc += srcRow[x + k] * kernel[k + radius];
                }

                dstRow[x] = acc;
            }

            for (int x = rightBorderStart; x < width; x++)
            {
                float acc = 0f;
                for (int k = -radius; k <= radius; k++)
                {
                    acc += srcRow[Reflect101Index(x + k, width)] * kernel[k + radius];
                }

                dstRow[x] = acc;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void ConvolveVerticalReflect101(
        ReadOnlySpan<float> src,
        Span<float> dst,
        int width,
        int height,
        ReadOnlySpan<float> kernel,
        int radius)
    {
        for (int y = 0; y < height; y++)
        {
            Span<float> dstRow = dst.Slice(y * width, width);
            dstRow.Clear();

            for (int k = -radius; k <= radius; k++)
            {
                float weight = kernel[k + radius];
                int sy = Reflect101Index(y + k, height);
                ReadOnlySpan<float> srcRow = src.Slice(sy * width, width);

                for (int x = 0; x < width; x++)
                {
                    dstRow[x] += srcRow[x] * weight;
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Reflect101Index(int index, int length)
    {
        if (length <= 1)
        {
            return 0;
        }

        if (index < 0)
        {
            index = -index;
        }

        if (index >= length)
        {
            index = (2 * length) - 2 - index;
        }

        return Math.Clamp(index, 0, length - 1);
    }
}
