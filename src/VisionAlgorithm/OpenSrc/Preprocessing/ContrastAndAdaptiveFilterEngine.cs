// File: src/VisionAlgorithm/OpenSrc/Preprocessing/ContrastAndAdaptiveFilterEngine.cs
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using OpenCvSharp;

namespace VisionAlgorithm.OpenSrc.Preprocessing;

/// <summary>
/// Contrast enhancement, adaptive binarization, frequency sharpening, and illumination normalization kernels
/// accelerated via zero-copy native OpenCV C++ AVX2/IPP routines (<see cref="Cv2.CreateCLAHE"/>, <see cref="Cv2.AdaptiveThreshold"/>,
/// <see cref="Cv2.BoxFilter"/>, <see cref="Cv2.GaussianBlur"/>, <see cref="Cv2.Sobel"/>) and <see cref="Vector256{T}"/> / <see cref="Vector512{T}"/> SIMD:
/// 1. Native C++ CLAHE (Contrast-Limited Adaptive Histogram Equalization, Zuiderveld 1994)
/// 2. Native C++ Box-Filter Sauvola &amp; Bradley-Roth Local Adaptive Thresholding ($O(1)$ per pixel)
/// 3. Native C++ Gaussian Unsharp Masking &amp; $3 \times 3$ Sobel Gradient Magnitude
/// 4. Hardware SIMD Z-Score Illumination Normalization
/// </summary>
public static class ContrastAndAdaptiveFilterEngine
{
    private const float MaxGray8Value = 255.0f;

    // [Math Why]: Standard dynamic range R = 128 for 8-bit grayscale in Sauvola's adaptive threshold formula (Sauvola & Pietikainen, 2000).
    private const float SauvolaDynamicRange8Bit = 128.0f;

    // [Math Why]: 3x3 Sobel operator sums 4 central differences, so 1/4 = 0.25 normalizes the directional gradient magnitude.
    private const double SobelQuarterScale = 0.25;

    // [Math Why]: Floor on standard deviation prevents division-by-zero explosion on flat uniform patches.
    public const float MinStandardDeviation = 1e-4f;

    /// <summary>
    /// Applies Contrast-Limited Adaptive Histogram Equalization (CLAHE, Zuiderveld 1994)
    /// via zero-copy native OpenCV C++ <see cref="CLAHE"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static unsafe void ApplyClahe(
        ReadOnlySpan<byte> source,
        int width,
        int height,
        int sourceStride,
        in ClaheFilterSpec spec,
        Span<byte> destination,
        int destinationStride)
    {
        spec.EnsureInitialized();
        EdgePreservingFilterEngine.ValidateBuffers(source, width, height, sourceStride, destination, destinationStride);

        int gridX = Math.Clamp(Math.Min(spec.TileGridX, Math.Max(1, width / 4)), 1, 32);
        int gridY = Math.Clamp(Math.Min(spec.TileGridY, Math.Max(1, height / 4)), 1, 32);

        fixed (byte* pSrc = source)
        fixed (byte* pDst = destination)
        {
            using var srcMat = Mat.FromPixelData(height, width, MatType.CV_8UC1, (nint)pSrc, sourceStride);
            using var dstMat = Mat.FromPixelData(height, width, MatType.CV_8UC1, (nint)pDst, destinationStride);
            using var clahe = Cv2.CreateCLAHE(spec.ClipLimit, new Size(gridX, gridY));
            clahe.Apply(srcMat, dstMat);
        }
    }

    /// <summary>
    /// Applies $O(1)$-per-pixel Local Mean (<see cref="Cv2.AdaptiveThreshold"/>) or Sauvola (<see cref="Cv2.BoxFilter"/>) Adaptive Thresholding.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static unsafe void ApplyAdaptiveThreshold(
        ReadOnlySpan<byte> source,
        int width,
        int height,
        int sourceStride,
        in AdaptiveThresholdSpec spec,
        Span<byte> destination,
        int destinationStride)
    {
        spec.EnsureInitialized();
        EdgePreservingFilterEngine.ValidateBuffers(source, width, height, sourceStride, destination, destinationStride);

        int ksize = (2 * spec.WindowRadius) + 1;

        if (!spec.UseSauvolaStdWeighting)
        {
            fixed (byte* pSrc = source)
            fixed (byte* pDst = destination)
            {
                using var srcMat = Mat.FromPixelData(height, width, MatType.CV_8UC1, (nint)pSrc, sourceStride);
                using var dstMat = Mat.FromPixelData(height, width, MatType.CV_8UC1, (nint)pDst, destinationStride);
                ThresholdTypes threshType = spec.InvertPolarity ? ThresholdTypes.BinaryInv : ThresholdTypes.Binary;
                Cv2.AdaptiveThreshold(srcMat, dstMat, MaxGray8Value, AdaptiveThresholdTypes.MeanC, threshType, ksize, spec.OffsetConstant);
            }
            return;
        }

        int totalPixels = checked(width * height);
        Size kernelSize = new(ksize, ksize);

        using var srcFloat = new Mat(height, width, MatType.CV_32FC1);
        using var srcSqFloat = new Mat(height, width, MatType.CV_32FC1);
        using var meanMat = new Mat(height, width, MatType.CV_32FC1);
        using var sqMeanMat = new Mat(height, width, MatType.CV_32FC1);

        fixed (byte* pSrc = source)
        {
            using var srcMat = Mat.FromPixelData(height, width, MatType.CV_8UC1, (nint)pSrc, sourceStride);
            srcMat.ConvertTo(srcFloat, MatType.CV_32FC1);
        }

        Cv2.Multiply(srcFloat, srcFloat, srcSqFloat);
        Cv2.BoxFilter(srcFloat, meanMat, MatType.CV_32FC1, kernelSize, new Point(-1, -1), normalize: true, BorderTypes.Replicate);
        Cv2.BoxFilter(srcSqFloat, sqMeanMat, MatType.CV_32FC1, kernelSize, new Point(-1, -1), normalize: true, BorderTypes.Replicate);

        var pMean = new ReadOnlySpan<float>((void*)meanMat.DataPointer, totalPixels);
        var pSqMean = new ReadOnlySpan<float>((void*)sqMeanMat.DataPointer, totalPixels);

        byte fg = spec.InvertPolarity ? (byte)0 : (byte)MaxGray8Value;
        byte bg = spec.InvertPolarity ? (byte)MaxGray8Value : (byte)0;
        float k = spec.SauvolaSensitivityK;
        float invR = 1.0f / SauvolaDynamicRange8Bit;
        float offset = spec.OffsetConstant;

        for (int y = 0; y < height; y++)
        {
            int rowOffset = y * width;
            ReadOnlySpan<byte> srcRow = source.Slice(y * sourceStride, width);
            ReadOnlySpan<float> meanRow = pMean.Slice(rowOffset, width);
            ReadOnlySpan<float> sqMeanRow = pSqMean.Slice(rowOffset, width);
            Span<byte> dstRow = destination.Slice(y * destinationStride, width);

            for (int x = 0; x < width; x++)
            {
                float mean = meanRow[x];
                float variance = MathF.Max(0f, sqMeanRow[x] - (mean * mean));
                float stdDev = MathF.Sqrt(variance);
                float threshold = (mean * (1f + (k * ((stdDev * invR) - 1f)))) - offset;
                dstRow[x] = srcRow[x] > threshold ? fg : bg;
            }
        }
    }

    /// <summary>
    /// Applies Unsharp Masking ($I_{\text{sharp}} = \operatorname{clamp}(I + \alpha (I - G_\sigma * I), 0, 255)$)
    /// via zero-copy native OpenCV C++ <see cref="Cv2.GaussianBlur"/> and <see cref="Cv2.AddWeighted"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static unsafe void ApplyUnsharpMask(
        ReadOnlySpan<byte> source,
        int width,
        int height,
        int sourceStride,
        in UnsharpSobelSpec spec,
        Span<byte> destination,
        int destinationStride)
    {
        spec.EnsureInitialized();
        EdgePreservingFilterEngine.ValidateBuffers(source, width, height, sourceStride, destination, destinationStride);

        int ksize = (2 * spec.Radius) + 1;
        using var blurMat = new Mat(height, width, MatType.CV_8UC1);

        fixed (byte* pSrc = source)
        fixed (byte* pDst = destination)
        {
            using var srcMat = Mat.FromPixelData(height, width, MatType.CV_8UC1, (nint)pSrc, sourceStride);
            using var dstMat = Mat.FromPixelData(height, width, MatType.CV_8UC1, (nint)pDst, destinationStride);
            Cv2.GaussianBlur(srcMat, blurMat, new Size(ksize, ksize), spec.Sigma, spec.Sigma, BorderTypes.Replicate);
            Cv2.AddWeighted(srcMat, 1.0 + spec.Amount, blurMat, -spec.Amount, 0.0, dstMat);
        }
    }

    /// <summary>
    /// Computes the $3 \times 3$ Sobel Gradient Magnitude $\operatorname{clamp}(\text{scale} \cdot \sqrt{G_x^2 + G_y^2}, 0, 255)$
    /// via zero-copy native OpenCV C++ <see cref="Cv2.Sobel"/> and <see cref="Cv2.Magnitude"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static unsafe void ApplySobelGradient(
        ReadOnlySpan<byte> source,
        int width,
        int height,
        int sourceStride,
        float scaleFactor,
        Span<byte> destination,
        int destinationStride)
    {
        if (!float.IsFinite(scaleFactor) || scaleFactor <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(scaleFactor), "scaleFactor must be finite and > 0.");
        }
        EdgePreservingFilterEngine.ValidateBuffers(source, width, height, sourceStride, destination, destinationStride);

        using var gxMat = new Mat(height, width, MatType.CV_32FC1);
        using var gyMat = new Mat(height, width, MatType.CV_32FC1);
        using var magMat = new Mat(height, width, MatType.CV_32FC1);

        fixed (byte* pSrc = source)
        fixed (byte* pDst = destination)
        {
            using var srcMat = Mat.FromPixelData(height, width, MatType.CV_8UC1, (nint)pSrc, sourceStride);
            using var dstMat = Mat.FromPixelData(height, width, MatType.CV_8UC1, (nint)pDst, destinationStride);
            Cv2.Sobel(srcMat, gxMat, MatType.CV_32FC1, 1, 0, ksize: 3, scale: SobelQuarterScale, delta: 0, BorderTypes.Replicate);
            Cv2.Sobel(srcMat, gyMat, MatType.CV_32FC1, 0, 1, ksize: 3, scale: SobelQuarterScale, delta: 0, BorderTypes.Replicate);
            Cv2.Magnitude(gxMat, gyMat, magMat);
            magMat.ConvertTo(dstMat, MatType.CV_8UC1, scaleFactor);
        }
    }

    /// <summary>
    /// Hardware-accelerated SIMD Z-score illumination normalizer computing $\hat{I}(x,y) = (I(x,y) - \mu) / \max(\sigma, 10^{-4})$.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void NormalizeZScore(ReadOnlySpan<float> source, Span<float> destination)
    {
        if (source.IsEmpty)
        {
            throw new ArgumentException("Source ROI pixel span must not be empty.", nameof(source));
        }
        if (destination.Length < source.Length)
        {
            throw new ArgumentException("Destination ROI span must be at least as long as source ROI span.", nameof(destination));
        }

        int count = source.Length;
        var (mean, stdDev) = ComputeMeanAndStdDev(source);
        float invStd = 1f / MathF.Max(stdDev, MinStandardDeviation);

        ref float srcBase = ref MemoryMarshal.GetReference(source);
        ref float dstBase = ref MemoryMarshal.GetReference(destination);

        int i = 0;
        if (Vector512.IsHardwareAccelerated && count >= Vector512<float>.Count)
        {
            int vecCount = Vector512<float>.Count;
            int limit = count & ~(vecCount - 1);
            Vector512<float> vMean = Vector512.Create(mean);
            Vector512<float> vInvStd = Vector512.Create(invStd);

            for (; i < limit; i += vecCount)
            {
                Vector512<float> v = Vector512.LoadUnsafe(ref srcBase, (nuint)i);
                Vector512<float> norm = (v - vMean) * vInvStd;
                norm.StoreUnsafe(ref dstBase, (nuint)i);
            }
        }
        else if (Vector256.IsHardwareAccelerated && count >= Vector256<float>.Count)
        {
            int vecCount = Vector256<float>.Count;
            int limit = count & ~(vecCount - 1);
            Vector256<float> vMean = Vector256.Create(mean);
            Vector256<float> vInvStd = Vector256.Create(invStd);

            for (; i < limit; i += vecCount)
            {
                Vector256<float> v = Vector256.LoadUnsafe(ref srcBase, (nuint)i);
                Vector256<float> norm = (v - vMean) * vInvStd;
                norm.StoreUnsafe(ref dstBase, (nuint)i);
            }
        }

        for (; i < count; i++)
        {
            Unsafe.Add(ref dstBase, i) = (Unsafe.Add(ref srcBase, i) - mean) * invStd;
        }
    }

    /// <summary>
    /// Computes the sample mean $\mu$ and population standard deviation $\sigma$ of a float span using <see cref="Vector256{T}"/> FMA reductions.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static (float Mean, float StdDev) ComputeMeanAndStdDev(ReadOnlySpan<float> values)
    {
        if (values.IsEmpty)
        {
            return (0f, 0f);
        }

        int count = values.Length;
        ref float baseRef = ref MemoryMarshal.GetReference(values);

        int i = 0;
        float sum = 0f;
        if (Vector256.IsHardwareAccelerated && count >= Vector256<float>.Count)
        {
            int vecCount = Vector256<float>.Count;
            int limit = count & ~(vecCount - 1);
            Vector256<float> vSum = Vector256<float>.Zero;
            for (; i < limit; i += vecCount)
            {
                vSum += Vector256.LoadUnsafe(ref baseRef, (nuint)i);
            }
            sum = Vector256.Sum(vSum);
        }

        for (; i < count; i++)
        {
            sum += Unsafe.Add(ref baseRef, i);
        }

        float mean = sum / count;

        int j = 0;
        float sqDiffSum = 0f;
        if (Vector256.IsHardwareAccelerated && count >= Vector256<float>.Count)
        {
            int vecCount = Vector256<float>.Count;
            int limit = count & ~(vecCount - 1);
            Vector256<float> vMean = Vector256.Create(mean);
            Vector256<float> vSqSum = Vector256<float>.Zero;
            for (; j < limit; j += vecCount)
            {
                Vector256<float> diff = Vector256.LoadUnsafe(ref baseRef, (nuint)j) - vMean;
                vSqSum = Fma.IsSupported ? Fma.MultiplyAdd(diff, diff, vSqSum) : vSqSum + (diff * diff);
            }
            sqDiffSum = Vector256.Sum(vSqSum);
        }

        for (; j < count; j++)
        {
            float diff = Unsafe.Add(ref baseRef, j) - mean;
            sqDiffSum += diff * diff;
        }

        float variance = MathF.Max(0f, sqDiffSum / count);
        return (mean, MathF.Sqrt(variance));
    }
}
