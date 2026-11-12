using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace VisionKernels;

/// <summary>
/// 2x octave image reduction kernels.
/// <para>
/// Provides both:
/// <list type="bullet">
/// <item><description><see cref="DownsampleBox2x2"/>: rounded 2x2 box average <c>(p00 + p10 + p01 + p11 + 2) / 4</c> (used by <c>CpuMatching</c> and <c>StegerShape</c>).</description></item>
/// <item><description><see cref="DownsampleBinomial5x5"/>: separable 5x5 binomial Gaussian <c>[1, 4, 6, 4, 1]^2 / 256</c> with <c>+128</c> rounding (used by <c>PatMax</c> <c>GranularityPyramid</c>).</description></item>
/// </list>
/// </para>
/// </summary>
public static class GaussianOctaveReducer
{
    private const int KernelCenterWeight = 6;
    private const int KernelNeighborWeight = 4;
    private const int NormalizationRounding = 128;
    private const int NormalizationShift = 8;
    public const int ScratchRowSlots = 5;
    private const int ParallelDownsampleThresholdPixels = 262_144;
    private const int ParallelStripHeight = 64;

    /// <summary>
    /// Downsamples an 8-bit image by 2x using a rounded 2x2 box filter <c>(sum + 2) &gt;&gt; 2</c> with SSE2 fast path.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void DownsampleBox2x2(
        ReadOnlySpan<byte> source,
        int srcWidth,
        int srcHeight,
        int srcStride,
        Span<byte> destination,
        int dstWidth,
        int dstHeight)
    {
        for (var y = 0; y < dstHeight; y++)
        {
            var x = 0;
            var top = 2 * y * srcStride;
            var bottom = Math.Min((2 * y) + 1, srcHeight - 1) * srcStride;
            if (Sse2.IsSupported)
            {
                for (; x <= dstWidth - 8; x += 8)
                {
                    var upper = Vector128.Create(source.Slice(top + (2 * x)));
                    var lower = Vector128.Create(source.Slice(bottom + (2 * x)));
                    var left = Sse2.Add(Sse2.UnpackLow(upper, Vector128<byte>.Zero).AsInt16(),
                        Sse2.UnpackLow(lower, Vector128<byte>.Zero).AsInt16());
                    var right = Sse2.Add(Sse2.UnpackHigh(upper, Vector128<byte>.Zero).AsInt16(),
                        Sse2.UnpackHigh(lower, Vector128<byte>.Zero).AsInt16());
                    var leftPairs = Sse2.MultiplyAddAdjacent(left, Vector128.Create((short)1));
                    var rightPairs = Sse2.MultiplyAddAdjacent(right, Vector128.Create((short)1));
                    leftPairs = Sse2.ShiftRightLogical(leftPairs + Vector128.Create(2), 2);
                    rightPairs = Sse2.ShiftRightLogical(rightPairs + Vector128.Create(2), 2);
                    var values = Sse2.PackUnsignedSaturate(Sse2.PackSignedSaturate(leftPairs, rightPairs), Vector128<short>.Zero);
                    var packed = values.AsUInt64().GetElement(0);
                    MemoryMarshal.Write(destination.Slice((y * dstWidth) + x), in packed);
                }
            }

            for (; x < dstWidth; x++)
            {
                var a = top + (2 * x);
                var right = Math.Min((2 * x) + 1, srcWidth - 1);
                destination[(y * dstWidth) + x] = (byte)((source[a] + source[top + right] +
                    source[bottom + (2 * x)] + source[bottom + right] + 2) / 4);
            }
        }
    }

    /// <summary>
    /// Downsamples an 8-bit image by 2x using separable 5x5 binomial Gaussian weights <c>[1, 4, 6, 4, 1]</c> and AVX2 integer SIMD.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void DownsampleBinomial5x5(
        ReadOnlySpan<byte> src,
        int srcWidth,
        int srcHeight,
        int srcStride,
        Span<byte> dst,
        int dstWidth,
        int dstHeight,
        int dstStride,
        Span<ushort> horizontalScratch)
    {
        if (srcWidth * srcHeight >= ParallelDownsampleThresholdPixels && dstHeight >= ParallelStripHeight * 2)
        {
            DownsampleBinomial5x5Parallel(src, srcWidth, srcHeight, srcStride, dst, dstWidth, dstHeight, dstStride);
            return;
        }

        DownsampleBinomial5x5Strip(
            src,
            srcWidth,
            srcHeight,
            srcStride,
            dst,
            dstWidth,
            dstStride,
            0,
            dstHeight,
            horizontalScratch);
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static unsafe void DownsampleBinomial5x5Parallel(
        ReadOnlySpan<byte> src,
        int srcWidth,
        int srcHeight,
        int srcStride,
        Span<byte> dst,
        int dstWidth,
        int dstHeight,
        int dstStride)
    {
        var stripCount = (dstHeight + ParallelStripHeight - 1) / ParallelStripHeight;
        var totalScratch = checked(stripCount * ScratchRowSlots * dstWidth);
        var scratchPool = ArrayPool<ushort>.Shared.Rent(totalScratch);
        try
        {
            fixed (byte* pSrc = src)
            fixed (byte* pDst = dst)
            fixed (ushort* pScratch = scratchPool)
            {
                var srcAddr = (nint)pSrc;
                var dstAddr = (nint)pDst;
                var scratchAddr = (nint)pScratch;
                var srcLen = srcHeight * srcStride;
                var dstLen = dstHeight * dstStride;

                Parallel.For(0, stripCount, s =>
                {
                    var startDstY = s * ParallelStripHeight;
                    var endDstY = Math.Min(dstHeight, startDstY + ParallelStripHeight);
                    var localSrc = new ReadOnlySpan<byte>((byte*)srcAddr, srcLen);
                    var localDst = new Span<byte>((byte*)dstAddr, dstLen);
                    var localScratch = new Span<ushort>((ushort*)scratchAddr + (s * ScratchRowSlots * dstWidth), ScratchRowSlots * dstWidth);
                    DownsampleBinomial5x5Strip(
                        localSrc,
                        srcWidth,
                        srcHeight,
                        srcStride,
                        localDst,
                        dstWidth,
                        dstStride,
                        startDstY,
                        endDstY,
                        localScratch);
                });
            }
        }
        finally
        {
            ArrayPool<ushort>.Shared.Return(scratchPool);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void DownsampleBinomial5x5Strip(
        ReadOnlySpan<byte> src,
        int srcWidth,
        int srcHeight,
        int srcStride,
        Span<byte> dst,
        int dstWidth,
        int dstStride,
        int startDstY,
        int endDstY,
        Span<ushort> horizontalScratch)
    {
        var computedRowUntil = Math.Max(0, (2 * startDstY) - 2) - 1;
        for (var dstY = startDstY; dstY < endDstY; dstY++)
        {
            var centerSrcY = 2 * dstY;
            var maxNeededY = Math.Min(srcHeight - 1, centerSrcY + 2);
            while (computedRowUntil < maxNeededY)
            {
                computedRowUntil++;
                var rowSlot = computedRowUntil % ScratchRowSlots;
                ConvolveHorizontalDecimatedRow(
                    src.Slice(computedRowUntil * srcStride, srcWidth),
                    horizontalScratch.Slice(rowSlot * dstWidth, dstWidth),
                    dstWidth);
            }

            var y0 = Math.Max(0, centerSrcY - 2) % ScratchRowSlots * dstWidth;
            var y1 = Math.Max(0, centerSrcY - 1) % ScratchRowSlots * dstWidth;
            var y2 = centerSrcY % ScratchRowSlots * dstWidth;
            var y3 = Math.Min(srcHeight - 1, centerSrcY + 1) % ScratchRowSlots * dstWidth;
            var y4 = Math.Min(srcHeight - 1, centerSrcY + 2) % ScratchRowSlots * dstWidth;

            ConvolveVerticalRow(
                horizontalScratch.Slice(y0, dstWidth),
                horizontalScratch.Slice(y1, dstWidth),
                horizontalScratch.Slice(y2, dstWidth),
                horizontalScratch.Slice(y3, dstWidth),
                horizontalScratch.Slice(y4, dstWidth),
                dst.Slice(dstY * dstStride, dstWidth));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void ConvolveHorizontalDecimatedRow(ReadOnlySpan<byte> srcRow, Span<ushort> dstRow, int dstWidth)
    {
        var srcWidth = srcRow.Length;
        ref var srcRef = ref MemoryMarshal.GetReference(srcRow);
        ref var dstRef = ref MemoryMarshal.GetReference(dstRow);

        var dstX = 0;
        if (dstWidth > 0)
        {
            var maxIdx0 = srcWidth - 1;
            var s0 = Unsafe.Add(ref srcRef, 0)
                + (KernelNeighborWeight * Unsafe.Add(ref srcRef, 0))
                + (KernelCenterWeight * Unsafe.Add(ref srcRef, 0))
                + (KernelNeighborWeight * Unsafe.Add(ref srcRef, Math.Min(maxIdx0, 1)))
                + Unsafe.Add(ref srcRef, Math.Min(maxIdx0, 2));
            Unsafe.Add(ref dstRef, 0) = (ushort)s0;
            dstX = 1;
        }

        // [Perf Why]: AVX2 16-lane horizontal 5-tap decimated convolution processes 16 destination pixels (32 source bytes) per cycle.
        if (Avx2.IsSupported && dstWidth >= 18)
        {
            var interiorEnd = dstWidth - 1;
            var simdEnd = 1 + ((interiorEnd - 1) & ~15);
            var vEvenMask = Vector256.Create((short)0x00FF);
            var vFour = Vector256.Create((short)KernelNeighborWeight);
            var vSix = Vector256.Create((short)KernelCenterWeight);

            for (; dstX < simdEnd; dstX += 16)
            {
                var centerSrcX = 2 * dstX;
                var wM2 = Vector256.LoadUnsafe(ref Unsafe.As<byte, short>(ref Unsafe.Add(ref srcRef, centerSrcX - 2)));
                var wP0 = Vector256.LoadUnsafe(ref Unsafe.As<byte, short>(ref Unsafe.Add(ref srcRef, centerSrcX)));
                var wP2 = Vector256.LoadUnsafe(ref Unsafe.As<byte, short>(ref Unsafe.Add(ref srcRef, centerSrcX + 2)));

                var x0 = Avx2.And(wM2, vEvenMask);
                var x1 = Avx2.ShiftRightLogical(wM2, 8);
                var x2 = Avx2.And(wP0, vEvenMask);
                var x3 = Avx2.ShiftRightLogical(wP0, 8);
                var x4 = Avx2.And(wP2, vEvenMask);

                var sum = x0 + (x1 * vFour) + (x2 * vSix) + (x3 * vFour) + x4;
                sum.AsUInt16().StoreUnsafe(ref Unsafe.Add(ref dstRef, dstX));
            }
        }

        for (; dstX < dstWidth; dstX++)
        {
            var center = 2 * dstX;
            var x0 = Math.Max(0, center - 2);
            var x1 = Math.Max(0, center - 1);
            var x2 = center;
            var x3 = Math.Min(srcWidth - 1, center + 1);
            var x4 = Math.Min(srcWidth - 1, center + 2);

            var sum = Unsafe.Add(ref srcRef, x0)
                + (KernelNeighborWeight * Unsafe.Add(ref srcRef, x1))
                + (KernelCenterWeight * Unsafe.Add(ref srcRef, x2))
                + (KernelNeighborWeight * Unsafe.Add(ref srcRef, x3))
                + Unsafe.Add(ref srcRef, x4);

            Unsafe.Add(ref dstRef, dstX) = (ushort)sum;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void ConvolveVerticalRow(
        ReadOnlySpan<ushort> r0,
        ReadOnlySpan<ushort> r1,
        ReadOnlySpan<ushort> r2,
        ReadOnlySpan<ushort> r3,
        ReadOnlySpan<ushort> r4,
        Span<byte> dstRow)
    {
        var width = dstRow.Length;
        ref var ref0 = ref MemoryMarshal.GetReference(r0);
        ref var ref1 = ref MemoryMarshal.GetReference(r1);
        ref var ref2 = ref MemoryMarshal.GetReference(r2);
        ref var ref3 = ref MemoryMarshal.GetReference(r3);
        ref var ref4 = ref MemoryMarshal.GetReference(r4);
        ref var dstRef = ref MemoryMarshal.GetReference(dstRow);

        var x = 0;
        // [Perf Why]: Compute 16 vertical 5-tap binomial sums simultaneously in Vector256<ushort> without 16-bit overflow (max sum = 255 * 256 = 65280 <= 65535).
        if (Avx2.IsSupported)
        {
            var simdLimit = width & ~(Vector256<ushort>.Count - 1);
            var vFour = Vector256.Create((ushort)KernelNeighborWeight);
            var vSix = Vector256.Create((ushort)KernelCenterWeight);
            var vRound = Vector256.Create((ushort)NormalizationRounding);

            for (; x < simdLimit; x += Vector256<ushort>.Count)
            {
                var v0 = Vector256.LoadUnsafe(ref Unsafe.Add(ref ref0, x));
                var v1 = Vector256.LoadUnsafe(ref Unsafe.Add(ref ref1, x));
                var v2 = Vector256.LoadUnsafe(ref Unsafe.Add(ref ref2, x));
                var v3 = Vector256.LoadUnsafe(ref Unsafe.Add(ref ref3, x));
                var v4 = Vector256.LoadUnsafe(ref Unsafe.Add(ref ref4, x));

                var sum = v0 + (v1 * vFour) + (v2 * vSix) + (v3 * vFour) + v4 + vRound;
                var shifted = Vector256.ShiftRightLogical(sum, NormalizationShift);
                var narrow = Vector128.Narrow(shifted.GetLower(), shifted.GetUpper());
                narrow.StoreUnsafe(ref Unsafe.Add(ref dstRef, x));
            }
        }

        for (; x < width; x++)
        {
            var sum = Unsafe.Add(ref ref0, x)
                + (KernelNeighborWeight * Unsafe.Add(ref ref1, x))
                + (KernelCenterWeight * Unsafe.Add(ref ref2, x))
                + (KernelNeighborWeight * Unsafe.Add(ref ref3, x))
                + Unsafe.Add(ref ref4, x)
                + NormalizationRounding;

            Unsafe.Add(ref dstRef, x) = (byte)(sum >> NormalizationShift);
        }
    }
}
