using System.Runtime.CompilerServices;

namespace VisionAlgorithm.OpenSrc.PatMax;

/// <summary>
/// Propagates attenuated unit normal vectors into zero-normal (below-threshold) pixels from their strongest 3x3 neighbor
/// so quantized coarse probe offsets tolerate sub-grid boundary phase shifts.
/// </summary>
internal static class ZeroNormalDilation
{
    public const float NeighborDilationFactor = 0.85f;
    private const int ParallelStripHeight = 64;
    private const int ParallelThresholdPixels = 262_144;

    public static void Dilate3x3(
        Span<float> unitX,
        Span<float> unitY,
        ReadOnlySpan<float> magnitude,
        int width,
        int height,
        float threshold)
    {
        if (width < 3 || height < 3)
        {
            return;
        }

        if (width * height >= ParallelThresholdPixels && height >= ParallelStripHeight * 2)
        {
            Dilate3x3Parallel(unitX, unitY, magnitude, width, height, threshold);
            return;
        }

        DilateStrip(unitX, unitY, magnitude, width, 1, height - 1, threshold);
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static unsafe void Dilate3x3Parallel(
        Span<float> unitX,
        Span<float> unitY,
        ReadOnlySpan<float> magnitude,
        int width,
        int height,
        float threshold)
    {
        // [Perf Why]: Each horizontal strip writes only to pixels where magnitude[idx] < threshold while reading magnitude >= threshold, preventing cross-strip data races.
        var stripCount = ((height - 2) + ParallelStripHeight - 1) / ParallelStripHeight;
        var totalPixels = width * height;
        fixed (float* pUx = unitX)
        fixed (float* pUy = unitY)
        fixed (float* pMag = magnitude)
        {
            var uxAddr = (nint)pUx;
            var uyAddr = (nint)pUy;
            var magAddr = (nint)pMag;

            Parallel.For(0, stripCount, s =>
            {
                var yStart = 1 + (s * ParallelStripHeight);
                var yEnd = Math.Min(height - 1, yStart + ParallelStripHeight);
                var localUx = new Span<float>((float*)uxAddr, totalPixels);
                var localUy = new Span<float>((float*)uyAddr, totalPixels);
                var localMag = new ReadOnlySpan<float>((float*)magAddr, totalPixels);
                DilateStrip(localUx, localUy, localMag, width, yStart, yEnd, threshold);
            });
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void DilateStrip(
        Span<float> unitX,
        Span<float> unitY,
        ReadOnlySpan<float> magnitude,
        int width,
        int yStart,
        int yEnd,
        float threshold)
    {
        for (var y = yStart; y < yEnd; y++)
        {
            var rowOffset = y * width;
            for (var x = 1; x < width - 1; x++)
            {
                var idx = rowOffset + x;
                if (magnitude[idx] >= threshold)
                {
                    continue;
                }

                var bestMag = threshold;
                var bestIdx = -1;
                for (var dy = -1; dy <= 1; dy++)
                {
                    var nRow = (y + dy) * width;
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var nIdx = nRow + x + dx;
                        var nMag = magnitude[nIdx];
                        if (nMag >= bestMag)
                        {
                            bestMag = nMag;
                            bestIdx = nIdx;
                        }
                    }
                }

                if (bestIdx >= 0)
                {
                    unitX[idx] = unitX[bestIdx] * NeighborDilationFactor;
                    unitY[idx] = unitY[bestIdx] * NeighborDilationFactor;
                }
            }
        }
    }
}
