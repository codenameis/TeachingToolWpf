using System.Buffers;
using System.Runtime.CompilerServices;
using VisionKernels;

namespace VisionAlgorithm.OpenSrc.PatMax;

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
    private const int ScratchRowSlots = GaussianOctaveReducer.ScratchRowSlots;

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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
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
        => GaussianOctaveReducer.DownsampleBinomial5x5(
            src,
            srcWidth,
            srcHeight,
            srcStride,
            dst,
            dstWidth,
            dstHeight,
            dstStride,
            horizontalScratch);

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
