// File: src/VisionAlgorithm/OpenSrc/PatMax/GranularityPyramid.cs
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace VisionAlgorithm.OpenSrc.PatMax;

/// <summary>
/// Read-only view of a single pyramid level with explicit stride and scale factor relative to the base image.
/// </summary>
public readonly struct PyramidLevelView
{
    private readonly byte[]? _buffer;

    public int LevelIndex { get; }
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public float ScaleFactor { get; }
    public bool HasPixels => _buffer is not null;

    public ReadOnlySpan<byte> Pixels => _buffer is not null ? _buffer.AsSpan(0, Height * Stride) : ReadOnlySpan<byte>.Empty;

    internal PyramidLevelView(byte[]? buffer, int levelIndex, int width, int height, int stride, float scaleFactor)
    {
        _buffer = buffer;
        LevelIndex = levelIndex;
        Width = width;
        Height = height;
        Stride = stride;
        ScaleFactor = scaleFactor;
    }
}

/// <summary>
/// Zero-allocation reusable Gaussian downsampling pyramid spanning Fine to Coarse Granularity (US 6,408,109).
/// </summary>
public sealed class GranularityPyramid : IDisposable
{
    private const int MinDimensionForDownsample = 8;
    private const int MinPatternSideForMultiOctave = 12;
    private const int MultiOctaveAutoBoostDimension = 48;
    private const int ThreeOctaveAutoBoostDimension = 96;
    private const int MinGuaranteedCoarseScale = 4;
    private const int PreferredLargePatternCoarseScale = 8;
    // [Math Why]: 5-tap binomial Gaussian weights [1, 4, 6, 4, 1] sum to 16; 2D separable product sums to 256 (shift >> 8 with +128 rounding).
    private const int KernelCenterWeight = 6;
    private const int KernelNeighborWeight = 4;
    private const int NormalizationRounding = 128;
    private const int NormalizationShift = 8;
    private const int ScratchRowSlots = 5;
    private const int ParallelDownsampleThresholdPixels = 262_144; // 512 x 512
    private const int ParallelStripHeight = 64;

    private byte[]?[] _levelBuffers = [];
    private int[] _widths = [];
    private int[] _heights = [];
    private int[] _strides = [];
    private float[] _scales = [];
    private ushort[]? _horizontalScratch;
    private bool _disposed;

    public int LevelCount { get; private set; }

    public PyramidLevelView GetLevel(int levelIndex)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(levelIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(levelIndex, LevelCount);

        return new PyramidLevelView(
            _levelBuffers[levelIndex]!,
            levelIndex,
            _widths[levelIndex],
            _heights[levelIndex],
            _strides[levelIndex],
            _scales[levelIndex]);
    }

    public static int ComputeOctaveCount(int fineGranularity, int coarseGranularity, int baseWidth, int baseHeight)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fineGranularity);
        ArgumentOutOfRangeException.ThrowIfLessThan(coarseGranularity, fineGranularity);

        var minBaseDim = Math.Min(baseWidth, baseHeight);
        var minSideLimit = minBaseDim >= MultiOctaveAutoBoostDimension
            ? MinPatternSideForMultiOctave
            : MinDimensionForDownsample;

        // [Math Why]: Interpret FineGranularity and CoarseGranularity as absolute scales relative to Scale=1 base image,
        // guaranteeing at least 2~3 octaves (1/4 or 1/8 scale) whenever pattern side length remains >= 12 px (and >= 14 px for 3-octave 1/8).
        var targetCoarseScale = coarseGranularity;
        if (minBaseDim >= MultiOctaveAutoBoostDimension)
        {
            targetCoarseScale = Math.Max(targetCoarseScale, Math.Max(MinGuaranteedCoarseScale, fineGranularity * 2));
            if (minBaseDim >= ThreeOctaveAutoBoostDimension && coarseGranularity >= MinGuaranteedCoarseScale && minBaseDim / PreferredLargePatternCoarseScale >= 14)
            {
                targetCoarseScale = Math.Max(targetCoarseScale, PreferredLargePatternCoarseScale);
            }
        }

        var count = 1;
        var currentScale = 1;
        var w = baseWidth;
        var h = baseHeight;
        while (currentScale * 2 <= targetCoarseScale &&
               w / 2 >= minSideLimit &&
               h / 2 >= minSideLimit)
        {
            currentScale *= 2;
            w /= 2;
            h /= 2;
            count++;
        }

        return count;
    }

    public void Build(
        ReadOnlySpan<byte> sourcePixels,
        int width,
        int height,
        int stride,
        int fineGranularity = PatMaxTrainParams.DefaultFineGranularity,
        int coarseGranularity = PatMaxTrainParams.DefaultCoarseGranularity,
        int baseScaleFactor = 1,
        bool copyBaseLevel = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(baseScaleFactor);
        if (sourcePixels.Length < ((height - 1) * stride) + width)
        {
            throw new ArgumentException("Source pixel buffer is smaller than required by height and stride.", nameof(sourcePixels));
        }

        var effectiveFine = Math.Max(fineGranularity, baseScaleFactor);
        var effectiveCoarse = Math.Max(effectiveFine, coarseGranularity);
        var targetLevels = ComputeOctaveCount(effectiveFine, effectiveCoarse, width, height);
        EnsureCapacity(targetLevels, width, height, allocateBaseLevel: copyBaseLevel);
        LevelCount = targetLevels;

        _widths[0] = width;
        _heights[0] = height;
        _strides[0] = copyBaseLevel ? width : stride;
        _scales[0] = baseScaleFactor;

        if (copyBaseLevel)
        {
            CopyBaseLevel(sourcePixels, width, height, stride, _levelBuffers[0]!);
        }

        for (var level = 1; level < targetLevels; level++)
        {
            var prevW = _widths[level - 1];
            var prevH = _heights[level - 1];
            var prevStride = _strides[level - 1];
            var dstW = prevW / 2;
            var dstH = prevH / 2;

            _widths[level] = dstW;
            _heights[level] = dstH;
            _strides[level] = dstW;
            _scales[level] = _scales[level - 1] * 2f;

            var prevSpan = level == 1 && !copyBaseLevel
                ? sourcePixels
                : _levelBuffers[level - 1]!.AsSpan(0, prevH * prevStride);

            DownsampleGaussianOctave(
                prevSpan,
                prevW,
                prevH,
                prevStride,
                _levelBuffers[level]!.AsSpan(0, dstH * dstW),
                dstW,
                dstH,
                dstW,
                _horizontalScratch!.AsSpan(0, ScratchRowSlots * dstW));
        }
    }

    private void EnsureCapacity(int requiredLevels, int baseWidth, int baseHeight, bool allocateBaseLevel = true)
    {
        if (_levelBuffers.Length < requiredLevels)
        {
            var oldLevels = _levelBuffers;
            _levelBuffers = new byte[]?[requiredLevels];
            Array.Copy(oldLevels, _levelBuffers, oldLevels.Length);
            _widths = new int[requiredLevels];
            _heights = new int[requiredLevels];
            _strides = new int[requiredLevels];
            _scales = new float[requiredLevels];
        }

        var w = baseWidth;
        var h = baseHeight;
        for (var i = 0; i < requiredLevels; i++)
        {
            if (i == 0 && !allocateBaseLevel)
            {
                w = Math.Max(1, w / 2);
                h = Math.Max(1, h / 2);
                continue;
            }

            var needed = checked(w * h);
            if (_levelBuffers[i] is null || _levelBuffers[i]!.Length < needed)
            {
                if (_levelBuffers[i] is { Length: > 0 } existing)
                {
                    ArrayPool<byte>.Shared.Return(existing);
                }

                _levelBuffers[i] = ArrayPool<byte>.Shared.Rent(needed);
            }

            w = Math.Max(1, w / 2);
            h = Math.Max(1, h / 2);
        }

        var scratchNeeded = checked(ScratchRowSlots * Math.Max(1, baseWidth / 2));
        if (_horizontalScratch is null || _horizontalScratch.Length < scratchNeeded)
        {
            if (_horizontalScratch is { Length: > 0 } existingScratch)
            {
                ArrayPool<ushort>.Shared.Return(existingScratch);
            }

            _horizontalScratch = ArrayPool<ushort>.Shared.Rent(scratchNeeded);
        }
    }

    private static void CopyBaseLevel(ReadOnlySpan<byte> src, int width, int height, int srcStride, Span<byte> dst)
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
    internal static void DownsampleGaussianOctave(
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
            DownsampleGaussianOctaveParallel(src, srcWidth, srcHeight, srcStride, dst, dstWidth, dstHeight, dstStride);
            return;
        }

        DownsampleGaussianOctaveStrip(
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
    private static unsafe void DownsampleGaussianOctaveParallel(
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
                    DownsampleGaussianOctaveStrip(
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
    private static void DownsampleGaussianOctaveStrip(
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
        if ( dstWidth > 0 )
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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        LevelCount = 0;
        for (var i = 0; i < _levelBuffers.Length; i++)
        {
            var buf = _levelBuffers[i];
            _levelBuffers[i] = null;
            if (buf is { Length: > 0 })
            {
                ArrayPool<byte>.Shared.Return(buf);
            }
        }

        var scratch = _horizontalScratch;
        _horizontalScratch = null;
        if (scratch is { Length: > 0 })
        {
            ArrayPool<ushort>.Shared.Return(scratch);
        }
    }
}
