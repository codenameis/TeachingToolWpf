// File: src/VisionAlgorithm/OpenSrc/Preprocessing/EdgePreservingFilterEngine.cs
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using OpenCvSharp;

namespace VisionAlgorithm.OpenSrc.Preprocessing;

/// <summary>
/// High-performance edge-preserving and impulse-noise preprocessing filters accelerated via
/// zero-copy native OpenCV C++ AVX2/IPP kernels (<see cref="Cv2.BoxFilter"/>, <see cref="Cv2.BilateralFilter"/>, <see cref="Cv2.MedianBlur"/>)
/// and hardware <see cref="Vector256{T}"/> FMA vectorization:
/// 1. $O(1)$-per-pixel Box-Filter Guided Image Filter (He et al., IEEE TPAMI 2013)
/// 2. Native C++ SIMD Bilateral Filter (Tomasi &amp; Manduchi, ICCV 1998)
/// 3. Native C++ SIMD Median Filter ($3 \times 3$ / $5 \times 5$ sorting networks &amp; $O(1)$ sliding histogram)
/// </summary>
public static class EdgePreservingFilterEngine
{
    private const float MaxGray8Value = 255.0f;

    /// <summary>
    /// Applies the $O(1)$-per-pixel Self-Guided Image Filter (He et al., TPAMI 2013):
    /// $a_k = \sigma_k^2 / (\sigma_k^2 + \epsilon)$, $b_k = (1 - a_k)\mu_k$, $q_i = \bar{a}_i I_i + \bar{b}_i$.
    /// Uses native OpenCV AVX2 <see cref="Cv2.BoxFilter"/> for the four $O(1)$ box aggregations and <see cref="Vector256{T}"/> SIMD for coefficient evaluation.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static unsafe void ApplyGuidedFilter(
        ReadOnlySpan<byte> source,
        int width,
        int height,
        int sourceStride,
        in GuidedFilterSpec spec,
        Span<byte> destination,
        int destinationStride)
    {
        spec.EnsureInitialized();
        ValidateBuffers(source, width, height, sourceStride, destination, destinationStride);

        int ksize = (2 * spec.Radius) + 1;
        Size kernelSize = new(ksize, ksize);
        float eps = spec.RegularizationEpsilon;
        int totalPixels = checked(width * height);

        using var srcFloat = new Mat(height, width, MatType.CV_32FC1);
        using var srcSqFloat = new Mat(height, width, MatType.CV_32FC1);
        using var meanI = new Mat(height, width, MatType.CV_32FC1);
        using var meanII = new Mat(height, width, MatType.CV_32FC1);
        using var coeffA = new Mat(height, width, MatType.CV_32FC1);
        using var coeffB = new Mat(height, width, MatType.CV_32FC1);
        using var meanA = new Mat(height, width, MatType.CV_32FC1);
        using var meanB = new Mat(height, width, MatType.CV_32FC1);

        fixed (byte* pSrc = source)
        {
            using var srcMat = Mat.FromPixelData(height, width, MatType.CV_8UC1, (nint)pSrc, sourceStride);
            srcMat.ConvertTo(srcFloat, MatType.CV_32FC1);
        }

        Cv2.Multiply(srcFloat, srcFloat, srcSqFloat);
        Cv2.BoxFilter(srcFloat, meanI, MatType.CV_32FC1, kernelSize, new Point(-1, -1), normalize: true, BorderTypes.Replicate);
        Cv2.BoxFilter(srcSqFloat, meanII, MatType.CV_32FC1, kernelSize, new Point(-1, -1), normalize: true, BorderTypes.Replicate);

        ComputeGuidedLinearCoefficientsSimd(
            new ReadOnlySpan<float>((void*)meanI.DataPointer, totalPixels),
            new ReadOnlySpan<float>((void*)meanII.DataPointer, totalPixels),
            eps,
            new Span<float>((void*)coeffA.DataPointer, totalPixels),
            new Span<float>((void*)coeffB.DataPointer, totalPixels));

        Cv2.BoxFilter(coeffA, meanA, MatType.CV_32FC1, kernelSize, new Point(-1, -1), normalize: true, BorderTypes.Replicate);
        Cv2.BoxFilter(coeffB, meanB, MatType.CV_32FC1, kernelSize, new Point(-1, -1), normalize: true, BorderTypes.Replicate);

        var pMeanA = new ReadOnlySpan<float>((void*)meanA.DataPointer, totalPixels);
        var pMeanB = new ReadOnlySpan<float>((void*)meanB.DataPointer, totalPixels);
        var pSrcFloat = new ReadOnlySpan<float>((void*)srcFloat.DataPointer, totalPixels);

        for (int y = 0; y < height; y++)
        {
            int rowOffset = y * width;
            ReadOnlySpan<float> rowA = pMeanA.Slice(rowOffset, width);
            ReadOnlySpan<float> rowB = pMeanB.Slice(rowOffset, width);
            ReadOnlySpan<float> rowI = pSrcFloat.Slice(rowOffset, width);
            Span<byte> dstRow = destination.Slice(y * destinationStride, width);

            for (int x = 0; x < width; x++)
            {
                float q = Math.Clamp(MathF.FusedMultiplyAdd(rowA[x], rowI[x], rowB[x]), 0f, MaxGray8Value);
                dstRow[x] = (byte)MathF.Round(q);
            }
        }
    }

    /// <summary>
    /// Applies edge-preserving Bilateral Filtering via zero-copy OpenCV native C++ SIMD <see cref="Cv2.BilateralFilter"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static unsafe void ApplyBilateralFilter(
        ReadOnlySpan<byte> source,
        int width,
        int height,
        int sourceStride,
        in BilateralFilterSpec spec,
        Span<byte> destination,
        int destinationStride)
    {
        spec.EnsureInitialized();
        ValidateBuffers(source, width, height, sourceStride, destination, destinationStride);

        int diameter = (2 * spec.Radius) + 1;
        bool isAlias = Unsafe.AreSame(
            ref MemoryMarshal.GetReference(source),
            ref MemoryMarshal.GetReference(destination));

        fixed (byte* pSrc = source)
        fixed (byte* pDst = destination)
        {
            using var srcMat = Mat.FromPixelData(height, width, MatType.CV_8UC1, (nint)pSrc, sourceStride);
            if (isAlias)
            {
                using var tempDst = new Mat(height, width, MatType.CV_8UC1);
                Cv2.BilateralFilter(srcMat, tempDst, diameter, spec.RangeSigma, spec.SpatialSigma, BorderTypes.Replicate);
                using var dstMat = Mat.FromPixelData(height, width, MatType.CV_8UC1, (nint)pDst, destinationStride);
                tempDst.CopyTo(dstMat);
            }
            else
            {
                using var dstMat = Mat.FromPixelData(height, width, MatType.CV_8UC1, (nint)pDst, destinationStride);
                Cv2.BilateralFilter(srcMat, dstMat, diameter, spec.RangeSigma, spec.SpatialSigma, BorderTypes.Replicate);
            }
        }
    }

    /// <summary>
    /// Applies impulse-noise Median Filtering via zero-copy OpenCV native C++ SIMD <see cref="Cv2.MedianBlur"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static unsafe void ApplyMedianFilter(
        ReadOnlySpan<byte> source,
        int width,
        int height,
        int sourceStride,
        int radius,
        Span<byte> destination,
        int destinationStride)
    {
        if (radius < 1 || radius > 31)
        {
            throw new ArgumentOutOfRangeException(nameof(radius), "Median filter radius must be in [1, 31].");
        }
        ValidateBuffers(source, width, height, sourceStride, destination, destinationStride);

        int ksize = (2 * radius) + 1;
        fixed (byte* pSrc = source)
        fixed (byte* pDst = destination)
        {
            using var srcMat = Mat.FromPixelData(height, width, MatType.CV_8UC1, (nint)pSrc, sourceStride);
            using var dstMat = Mat.FromPixelData(height, width, MatType.CV_8UC1, (nint)pDst, destinationStride);
            Cv2.MedianBlur(srcMat, dstMat, ksize);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void ComputeGuidedLinearCoefficientsSimd(
        ReadOnlySpan<float> meanI,
        ReadOnlySpan<float> meanII,
        float eps,
        Span<float> coeffA,
        Span<float> coeffB)
    {
        int count = meanI.Length;
        ref float mIRef = ref MemoryMarshal.GetReference(meanI);
        ref float mIIRef = ref MemoryMarshal.GetReference(meanII);
        ref float aRef = ref MemoryMarshal.GetReference(coeffA);
        ref float bRef = ref MemoryMarshal.GetReference(coeffB);

        int i = 0;
        if (Vector256.IsHardwareAccelerated && count >= Vector256<float>.Count)
        {
            int vecCount = Vector256<float>.Count;
            int limit = count & ~(vecCount - 1);
            Vector256<float> vZero = Vector256<float>.Zero;
            Vector256<float> vOne = Vector256.Create(1.0f);
            Vector256<float> vEps = Vector256.Create(eps);

            for (; i < limit; i += vecCount)
            {
                Vector256<float> vMean = Vector256.LoadUnsafe(ref mIRef, (nuint)i);
                Vector256<float> vSqMean = Vector256.LoadUnsafe(ref mIIRef, (nuint)i);
                Vector256<float> vVar = Vector256.Max(vZero, vSqMean - (vMean * vMean));
                Vector256<float> vA = vVar / (vVar + vEps);
                Vector256<float> vB = (vOne - vA) * vMean;

                vA.StoreUnsafe(ref aRef, (nuint)i);
                vB.StoreUnsafe(ref bRef, (nuint)i);
            }
        }

        for (; i < count; i++)
        {
            float mean = Unsafe.Add(ref mIRef, i);
            float sqMean = Unsafe.Add(ref mIIRef, i);
            float variance = MathF.Max(0f, sqMean - (mean * mean));
            float a = variance / (variance + eps);
            float b = (1f - a) * mean;
            Unsafe.Add(ref aRef, i) = a;
            Unsafe.Add(ref bRef, i) = b;
        }
    }

    internal static void ValidateBuffers(
        ReadOnlySpan<byte> source,
        int width,
        int height,
        int sourceStride,
        Span<byte> destination,
        int destinationStride)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Width must be > 0.");
        }
        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), "Height must be > 0.");
        }
        if (sourceStride < width)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceStride), "sourceStride must be >= width.");
        }
        if (destinationStride < width)
        {
            throw new ArgumentOutOfRangeException(nameof(destinationStride), "destinationStride must be >= width.");
        }
        int minSrcLen = ((height - 1) * sourceStride) + width;
        int minDstLen = ((height - 1) * destinationStride) + width;
        if (source.Length < minSrcLen)
        {
            throw new ArgumentException("Source buffer is smaller than required strided dimensions.", nameof(source));
        }
        if (destination.Length < minDstLen)
        {
            throw new ArgumentException("Destination buffer is smaller than required strided dimensions.", nameof(destination));
        }
    }
}
