using global::ILGPU;
using global::ILGPU.Algorithms;
using VisionAlgorithm.OpenSrc.PatternMatching;

namespace VisionCompute.ILGPU;

internal static class CudaCandidateKernels
{
    public static void SpreadHorizontal(Index1D index, ArrayView<byte> input, ArrayView<byte> output,
        int width, int height, int radius)
    {
        var i = (int)index;
        if (i >= width * height)
        {
            return;
        }
        var x = i % width;
        var row = i - x;
        byte bits = 0;
        for (var sample = XMath.Max(0, x - radius); sample <= XMath.Min(width - 1, x + radius); sample++)
        {
            bits |= input[row + sample];
        }
        output[i] = bits;
    }

    public static void SpreadVerticalAndRespond(Index1D index, ArrayView<byte> input, ArrayView<byte> responses,
        ArrayView<byte> lookup, int width, int height, int radius)
    {
        var i = (int)index;
        var count = width * height;
        if (i >= count)
        {
            return;
        }
        var x = i % width;
        var y = i / width;
        byte bits = 0;
        for (var sample = XMath.Max(0, y - radius); sample <= XMath.Min(height - 1, y + radius); sample++)
        {
            bits |= input[(sample * width) + x];
        }
        for (var bin = 0; bin < 8; bin++)
        {
            responses[(bin * count) + i] = lookup[(bin * 256) + bits];
        }
    }

    public static void Accumulate(Index1D index, ArrayView<byte> responses,
        ArrayView<CandidateScoreFeature> features, ArrayView<int> templateOffsets, ArrayView<ushort> scores,
        int width, int height, int step, int gridWidth, int gridHeight, int templateCount)
    {
        var i = (int)index;
        var gridCount = gridWidth * gridHeight;
        if (i >= gridCount * templateCount)
        {
            return;
        }
        var template = i / gridCount;
        var cell = i % gridCount;
        var x = cell % gridWidth * step;
        var y = cell / gridWidth * step;
        var planeCount = width * height;
        var sum = 0;
        for (var featureIndex = templateOffsets[template]; featureIndex < templateOffsets[template + 1]; featureIndex++)
        {
            var feature = features[featureIndex];
            var sampleX = x + feature.X;
            var sampleY = y + feature.Y;
            if (sampleX >= 0 && sampleY >= 0 && sampleX < width && sampleY < height)
            {
                sum += responses[(feature.Bin * planeCount) + (sampleY * width) + sampleX];
            }
        }
        scores[i] = (ushort)sum;
    }
}
