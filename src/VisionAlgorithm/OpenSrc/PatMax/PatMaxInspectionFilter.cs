using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace VisionAlgorithm.OpenSrc.PatMax;

/// <summary>
/// Zero-allocation SIMD preprocessing filter pipeline shared identically between live ROI filter preview,
/// PatMax pattern training, and PatMax runtime inspection.
/// </summary>
public static class PatMaxInspectionFilter
{
    private const int MinFilterDimension = 3;
    private const int GaussianWindowRows = 5;
    private const int BorderMargin = 2;
    private const float GaussianKernelCenter = 6.0f;
    private const float GaussianKernelNeighbor = 4.0f;
    private const float GaussianNormScale = 1.0f / 16.0f;
    private const float MaxByteFloat = 255.0f;
    private const float SobelDisplayGain = 2.0f;
    private const float UnsharpDefaultGain = 1.25f;

    /// <summary>
    /// Returns true when the given filter configuration requires an extra preprocessing pass prior to <see cref="SubPixelEdgeExtractor"/>.
    /// <see cref="PatMaxPreprocessingFilterKind.SobelEdgeResponsePreview"/> at default scale (<c>FineGranularity &lt;= 1</c> and <c>FilterSigma &lt;= 1.25</c>)
    /// relies directly on <see cref="SubPixelEdgeExtractor"/>'s built-in 5x5 Gaussian + 3x3 Scharr kernel to avoid double-smoothing fine edges.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool RequiresRuntimePreprocessingPass(in PatMaxTrainParams trainParams)
    {
        return trainParams.PreprocessingFilter switch
        {
            PatMaxPreprocessingFilterKind.None => false,
            PatMaxPreprocessingFilterKind.SobelEdgeResponsePreview => trainParams.FineGranularity > 1 || trainParams.FilterSigma > 1.25f,
            _ => true
        };
    }

    /// <summary>
    /// Applies the active PatMax inspection preprocessing filter to <paramref name="source"/> and writes
    /// the filtered grayscale pixels into <paramref name="destination"/> (width * height).
    /// When <paramref name="forDisplayPreview"/> is true and the mode is <see cref="PatMaxPreprocessingFilterKind.SobelEdgeResponsePreview"/>,
    /// outputs the thresholded Gaussian-Sobel edge response map so the user can visually tune <paramref name="edgeThreshold"/> and granularity.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void Apply(
        ReadOnlySpan<byte> source,
        int width,
        int height,
        int srcStride,
        Span<byte> destination,
        PatMaxPreprocessingFilterKind kind,
        int fineGranularity,
        float filterSigma,
        float edgeThreshold,
        bool forDisplayPreview = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfLessThan(srcStride, width);

        var pixelCount = checked(width * height);
        if (source.Length < ((height - 1) * srcStride) + width)
        {
            throw new ArgumentException("Source span is smaller than required by height and stride.", nameof(source));
        }

        if (destination.Length < pixelCount)
        {
            throw new ArgumentException("Destination span must be at least width * height bytes.", nameof(destination));
        }

        if (width < MinFilterDimension || height < MinFilterDimension || kind == PatMaxPreprocessingFilterKind.None)
        {
            CopyStridedToContiguous(source, width, height, srcStride, destination);
            return;
        }

        var passes = Math.Clamp((int)MathF.Round(MathF.Max(1.0f, filterSigma) * Math.Max(1, fineGranularity * 0.5f)), 1, 3);

        switch (kind)
        {
            case PatMaxPreprocessingFilterKind.GaussianScaleSpace:
                ApplyGaussianPasses(source, width, height, srcStride, destination, passes);
                break;

            case PatMaxPreprocessingFilterKind.MedianDenoise:
                ApplyMedian3x3(source, width, height, srcStride, destination);
                if (passes > 1)
                {
                    var tempPool = ArrayPool<byte>.Shared.Rent(pixelCount);
                    try
                    {
                        var tempSpan = tempPool.AsSpan(0, pixelCount);
                        destination.Slice(0, pixelCount).CopyTo(tempSpan);
                        ApplyGaussianPasses(tempSpan, width, height, width, destination, passes - 1);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(tempPool);
                    }
                }
                break;

            case PatMaxPreprocessingFilterKind.EdgeContrastEnhance:
                ApplyUnsharpEdgeEnhance(source, width, height, srcStride, destination, passes, filterSigma);
                break;

            case PatMaxPreprocessingFilterKind.SobelEdgeResponsePreview:
                if (forDisplayPreview)
                {
                    RenderSobelEdgeResponseMap(source, width, height, srcStride, destination, Math.Max(0, passes - 1), edgeThreshold);
                }
                else if (passes > 1)
                {
                    // SubPixelEdgeExtractor already performs 1 pass of 5x5 Gaussian smoothing; apply only additional scale-space passes.
                    ApplyGaussianPasses(source, width, height, srcStride, destination, passes - 1);
                }
                else
                {
                    CopyStridedToContiguous(source, width, height, srcStride, destination);
                }
                break;

            default:
                CopyStridedToContiguous(source, width, height, srcStride, destination);
                break;
        }
    }

    private static void CopyStridedToContiguous(ReadOnlySpan<byte> src, int width, int height, int srcStride, Span<byte> dst)
    {
        if (srcStride == width)
        {
            src.Slice(0, width * height).CopyTo(dst);
            return;
        }

        for (var y = 0; y < height; y++)
        {
            src.Slice(y * srcStride, width).CopyTo(dst.Slice(y * width, width));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void ApplyGaussianPasses(
        ReadOnlySpan<byte> source,
        int width,
        int height,
        int srcStride,
        Span<byte> destination,
        int passes)
    {
        var pixelCount = width * height;
        var ringLen = width * GaussianWindowRows;
        var floatPool = ArrayPool<float>.Shared.Rent(ringLen);
        byte[]? pingPongPool = passes > 1 ? ArrayPool<byte>.Shared.Rent(pixelCount) : null;

        try
        {
            var ringSpan = floatPool.AsSpan(0, ringLen);
            ApplySingleGaussian5x5(source, width, height, srcStride, destination, ringSpan);

            if (passes > 1 && pingPongPool is not null)
            {
                var tempSpan = pingPongPool.AsSpan(0, pixelCount);
                for (var p = 1; p < passes; p++)
                {
                    destination.Slice(0, pixelCount).CopyTo(tempSpan);
                    ApplySingleGaussian5x5(tempSpan, width, height, width, destination, ringSpan);
                }
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(floatPool);
            if (pingPongPool is not null)
            {
                ArrayPool<byte>.Shared.Return(pingPongPool);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void ApplySingleGaussian5x5(
        ReadOnlySpan<byte> src,
        int width,
        int height,
        int srcStride,
        Span<byte> dst,
        Span<float> horizRing)
    {
        if (width < GaussianWindowRows || height < GaussianWindowRows)
        {
            CopyStridedToContiguous(src, width, height, srcStride, dst);
            return;
        }

        var nextHorizY = 0;
        for (var y = 0; y < height; y++)
        {
            var maxNeededY = Math.Min(height - 1, y + BorderMargin);
            while (nextHorizY <= maxNeededY)
            {
                SmoothHorizontalRow(
                    src.Slice(nextHorizY * srcStride, width),
                    horizRing.Slice((nextHorizY % GaussianWindowRows) * width, width));
                nextHorizY++;
            }

            SmoothVerticalRowToByte(
                horizRing,
                width,
                height,
                y,
                dst.Slice(y * width, width));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void SmoothHorizontalRow(ReadOnlySpan<byte> src, Span<float> dst)
    {
        var width = src.Length;
        ref var srcRef = ref MemoryMarshal.GetReference(src);
        ref var dstRef = ref MemoryMarshal.GetReference(dst);

        for (var x = 0; x < BorderMargin; x++)
        {
            var maxIdx = width - 1;
            var sum = src[Math.Max(0, x - 2)]
                + (GaussianKernelNeighbor * src[Math.Max(0, x - 1)])
                + (GaussianKernelCenter * src[x])
                + (GaussianKernelNeighbor * src[Math.Min(maxIdx, x + 1)])
                + src[Math.Min(maxIdx, x + 2)];
            Unsafe.Add(ref dstRef, x) = sum * GaussianNormScale;
        }

        var xIndex = BorderMargin;
        var interiorEnd = width - BorderMargin;
        for (; xIndex < interiorEnd; xIndex++)
        {
            var sum = Unsafe.Add(ref srcRef, xIndex - 2)
                + (GaussianKernelNeighbor * Unsafe.Add(ref srcRef, xIndex - 1))
                + (GaussianKernelCenter * Unsafe.Add(ref srcRef, xIndex))
                + (GaussianKernelNeighbor * Unsafe.Add(ref srcRef, xIndex + 1))
                + Unsafe.Add(ref srcRef, xIndex + 2);
            Unsafe.Add(ref dstRef, xIndex) = sum * GaussianNormScale;
        }

        for (; xIndex < width; xIndex++)
        {
            var maxIdx = width - 1;
            var sum = src[Math.Max(0, xIndex - 2)]
                + (GaussianKernelNeighbor * src[Math.Max(0, xIndex - 1)])
                + (GaussianKernelCenter * src[xIndex])
                + (GaussianKernelNeighbor * src[Math.Min(maxIdx, xIndex + 1)])
                + src[Math.Min(maxIdx, xIndex + 2)];
            Unsafe.Add(ref dstRef, xIndex) = sum * GaussianNormScale;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void SmoothVerticalRowToByte(
        ReadOnlySpan<float> horizRing,
        int width,
        int height,
        int y,
        Span<byte> dst)
    {
        var r0Offset = (Math.Max(0, y - 2) % GaussianWindowRows) * width;
        var r1Offset = (Math.Max(0, y - 1) % GaussianWindowRows) * width;
        var r2Offset = (y % GaussianWindowRows) * width;
        var r3Offset = (Math.Min(height - 1, y + 1) % GaussianWindowRows) * width;
        var r4Offset = (Math.Min(height - 1, y + 2) % GaussianWindowRows) * width;

        ref var ringRef = ref MemoryMarshal.GetReference(horizRing);
        ref var dstRef = ref MemoryMarshal.GetReference(dst);

        var x = 0;
        if (Avx2.IsSupported)
        {
            var simdLimit = width & ~(Vector256<float>.Count - 1);
            var vFour = Vector256.Create(GaussianKernelNeighbor);
            var vSix = Vector256.Create(GaussianKernelCenter);
            var vNorm = Vector256.Create(GaussianNormScale);
            var vHalf = Vector256.Create(0.5f);
            var vZero = Vector256<float>.Zero;
            var vMax = Vector256.Create(MaxByteFloat);

            for (; x < simdLimit; x += Vector256<float>.Count)
            {
                var v0 = Vector256.LoadUnsafe(ref Unsafe.Add(ref ringRef, r0Offset + x));
                var v1 = Vector256.LoadUnsafe(ref Unsafe.Add(ref ringRef, r1Offset + x));
                var v2 = Vector256.LoadUnsafe(ref Unsafe.Add(ref ringRef, r2Offset + x));
                var v3 = Vector256.LoadUnsafe(ref Unsafe.Add(ref ringRef, r3Offset + x));
                var v4 = Vector256.LoadUnsafe(ref Unsafe.Add(ref ringRef, r4Offset + x));

                var sum = v0 + v4 + ((v1 + v3) * vFour) + (v2 * vSix);
                var val = Vector256.Min(vMax, Vector256.Max(vZero, (sum * vNorm) + vHalf));
                var ints = Avx.ConvertToVector256Int32WithTruncation(val);
                for (var k = 0; k < Vector256<float>.Count; k++)
                {
                    Unsafe.Add(ref dstRef, x + k) = (byte)ints.GetElement(k);
                }
            }
        }

        for (; x < width; x++)
        {
            var sum = Unsafe.Add(ref ringRef, r0Offset + x)
                + (GaussianKernelNeighbor * Unsafe.Add(ref ringRef, r1Offset + x))
                + (GaussianKernelCenter * Unsafe.Add(ref ringRef, r2Offset + x))
                + (GaussianKernelNeighbor * Unsafe.Add(ref ringRef, r3Offset + x))
                + Unsafe.Add(ref ringRef, r4Offset + x);
            var v = (int)((sum * GaussianNormScale) + 0.5f);
            Unsafe.Add(ref dstRef, x) = (byte)Math.Clamp(v, 0, byte.MaxValue);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void ApplyMedian3x3(
        ReadOnlySpan<byte> src,
        int width,
        int height,
        int srcStride,
        Span<byte> dst)
    {
        // Copy top and bottom border rows directly
        src.Slice(0, width).CopyTo(dst.Slice(0, width));
        src.Slice((height - 1) * srcStride, width).CopyTo(dst.Slice((height - 1) * width, width));

        Span<byte> win = stackalloc byte[9];
        for (var y = 1; y < height - 1; y++)
        {
            var r0 = src.Slice((y - 1) * srcStride, width);
            var r1 = src.Slice(y * srcStride, width);
            var r2 = src.Slice((y + 1) * srcStride, width);
            var dstRow = dst.Slice(y * width, width);

            dstRow[0] = r1[0];
            dstRow[width - 1] = r1[width - 1];

            for (var x = 1; x < width - 1; x++)
            {
                win[0] = r0[x - 1];
                win[1] = r0[x];
                win[2] = r0[x + 1];
                win[3] = r1[x - 1];
                win[4] = r1[x];
                win[5] = r1[x + 1];
                win[6] = r2[x - 1];
                win[7] = r2[x];
                win[8] = r2[x + 1];

                // Partial insertion sort up to median index 4
                for (var i = 1; i < 9; i++)
                {
                    var key = win[i];
                    var j = i - 1;
                    while (j >= 0 && win[j] > key)
                    {
                        win[j + 1] = win[j];
                        j--;
                    }
                    win[j + 1] = key;
                }

                dstRow[x] = win[4];
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void ApplyUnsharpEdgeEnhance(
        ReadOnlySpan<byte> src,
        int width,
        int height,
        int srcStride,
        Span<byte> dst,
        int passes,
        float filterSigma)
    {
        var pixelCount = width * height;
        var smoothPool = ArrayPool<byte>.Shared.Rent(pixelCount);
        try
        {
            var smoothSpan = smoothPool.AsSpan(0, pixelCount);
            if (passes > 0)
            {
                ApplyGaussianPasses(src, width, height, srcStride, smoothSpan, passes);
            }
            else
            {
                CopyStridedToContiguous(src, width, height, srcStride, smoothSpan);
            }

            var gain = Math.Clamp(filterSigma > 0.0f ? filterSigma : UnsharpDefaultGain, 0.25f, 3.0f);
            for (var y = 0; y < height; y++)
            {
                var srcRow = src.Slice(y * srcStride, width);
                var blurRow = smoothSpan.Slice(y * width, width);
                var dstRow = dst.Slice(y * width, width);
                for (var x = 0; x < width; x++)
                {
                    var orig = srcRow[x];
                    var diff = orig - blurRow[x];
                    var enhanced = (int)(orig + (diff * gain) + 0.5f);
                    dstRow[x] = (byte)Math.Clamp(enhanced, 0, byte.MaxValue);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(smoothPool);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void RenderSobelEdgeResponseMap(
        ReadOnlySpan<byte> src,
        int width,
        int height,
        int srcStride,
        Span<byte> dst,
        int passes,
        float edgeThreshold)
    {
        var pixelCount = width * height;
        var smoothPool = ArrayPool<byte>.Shared.Rent(pixelCount);
        var floatPool = ArrayPool<float>.Shared.Rent(pixelCount * 3);
        try
        {
            var smoothSpan = smoothPool.AsSpan(0, pixelCount);
            if (passes > 0)
            {
                ApplyGaussianPasses(src, width, height, srcStride, smoothSpan, passes);
            }
            else
            {
                CopyStridedToContiguous(src, width, height, srcStride, smoothSpan);
            }

            var floatSpan = floatPool.AsSpan(0, pixelCount * 3);
            var unitX = floatSpan.Slice(0, pixelCount);
            var unitY = floatSpan.Slice(pixelCount, pixelCount);
            var mag = floatSpan.Slice(pixelCount * 2, pixelCount);

            var safeThreshold = MathF.Max(0.5f, edgeThreshold);
            SubPixelEdgeExtractor.ComputeDenseUnitNormals(
                smoothSpan,
                width,
                height,
                width,
                safeThreshold,
                unitX,
                unitY,
                mag);

            for (var i = 0; i < pixelCount; i++)
            {
                var m = mag[i];
                if (m < safeThreshold)
                {
                    // Dimmed background context (25% original intensity) so the user sees where edges lie inside the ROI
                    dst[i] = (byte)(smoothSpan[i] >> 2);
                }
                else
                {
                    // Active edge response passing EdgeThreshold highlighted brightly (128..255)
                    var boosted = 128 + (int)((m - safeThreshold) * SobelDisplayGain);
                    dst[i] = (byte)Math.Clamp(boosted, 128, byte.MaxValue);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(smoothPool);
            ArrayPool<float>.Shared.Return(floatPool);
        }
    }
}
