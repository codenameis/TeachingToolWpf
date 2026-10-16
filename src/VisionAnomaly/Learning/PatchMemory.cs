using System.Numerics;

namespace VisionAnomaly.Learning;

internal static class PatchMemory
{
    public static float Distance(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        var sum = Vector<float>.Zero;
        var offset = 0;
        for (; offset <= left.Length - Vector<float>.Count; offset += Vector<float>.Count)
        {
            var difference = new Vector<float>(left.Slice(offset)) - new Vector<float>(right.Slice(offset));
            sum += difference * difference;
        }
        var distance = Vector.Sum(sum);
        for (; offset < left.Length; offset++)
        {
            distance += MathF.Pow(left[offset] - right[offset], 2);
        }

        return distance;
    }

    public static float[] Score(float[] patches, float[] bank, CancellationToken token)
    {
        const int Dimensions = PatchFeatureExtractor.Dimensions;
        var scores = new float[patches.Length / Dimensions];
        for (var patch = 0; patch < scores.Length; patch++)
        {
            token.ThrowIfCancellationRequested();
            var nearest = float.MaxValue;
            for (var candidate = 0; candidate < bank.Length; candidate += Dimensions)
            {
                nearest = Math.Min(nearest, Distance(patches.AsSpan(patch * Dimensions, Dimensions), bank.AsSpan(candidate, Dimensions)));
            }

            scores[patch] = MathF.Sqrt(nearest / Dimensions);
        }
        return scores;
    }

    public static float[] SelectCoreset(IReadOnlyList<float[]> candidates, int limit, CancellationToken token)
    {
        const int Dimensions = PatchFeatureExtractor.Dimensions;
        var count = Math.Min(limit, candidates.Count);
        var bank = new float[count * Dimensions];
        var distances = Enumerable.Repeat(float.MaxValue, candidates.Count).ToArray();
        var selected = 0;
        for (var index = 0; index < count; index++)
        {
            token.ThrowIfCancellationRequested();
            var current = candidates[selected];
            current.CopyTo(bank, index * Dimensions);
            var farthest = -1f;
            for (var next = 0; next < candidates.Count; next++)
            {
                distances[next] = Math.Min(distances[next], Distance(current, candidates[next]));
                if (distances[next] > farthest)
                {
                    farthest = distances[next];
                    selected = next;
                }
            }
        }
        return bank;
    }
}
