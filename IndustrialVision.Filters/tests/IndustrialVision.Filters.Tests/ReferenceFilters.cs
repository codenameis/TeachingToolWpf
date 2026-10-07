using IndustrialVision.Filters.Core;

namespace IndustrialVision.Filters.Tests;

// 테스트 전용: SIMD/분리 convolution/deque/GPU 커널을 재사용하지 않는다.
internal static class ReferenceFilters
{
    internal static double[] Execute(float[] input, int width, int height, Recipe recipe)
    {
        var current = input.Select(v => (double)v).ToArray();
        foreach (var filter in recipe.Filters)
        {
            if (filter is FlatField flat)
            {
                for (var i = 0; i < current.Length; i++)
                {
                    var dark = flat.Dark is null ? 0 : flat.Dark.Values[i];
                    double map = flat.Map.Values[i];
                    current[i] = flat.Kind == FlatFieldKind.RawReference
                        ? (map - dark > flat.MinimumDenominator ? (current[i] - dark) * flat.Scale / (map - dark) : double.NaN)
                        : (map > 0 ? (current[i] - dark) * map * flat.Scale : double.NaN);
                }
            }
            else if (filter is Gaussian gaussian)
            {
                var next = new double[current.Length];
                var radius = gaussian.Radius;
                double norm = 0;
                for (var dy = -radius; dy <= radius; dy++)
                {
                    for (var dx = -radius; dx <= radius; dx++)
                    {
                        norm += Math.Exp(-((dx * dx) + (dy * dy)) / (2 * gaussian.Sigma * gaussian.Sigma));
                    }
                }
                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        double sum = 0;
                        for (var dy = -radius; dy <= radius; dy++)
                        {
                            for (var dx = -radius; dx <= radius; dx++)
                            {
                                var xx = Math.Min(width - 1, Math.Max(0, x + dx));
                                var yy = Math.Min(height - 1, Math.Max(0, y + dy));
                                sum += current[(yy * width) + xx] * Math.Exp(-((dx * dx) + (dy * dy)) / (2 * gaussian.Sigma * gaussian.Sigma));
                            }
                        }
                        next[(y * width) + x] = sum / norm;
                    }
                }
                current = next;
            }
            else if (filter is Morphology morphology)
            {
                var maximum = morphology.Kind is MorphologyKind.Dilation or MorphologyKind.Closing or MorphologyKind.BlackHat;
                var next = Extrema(current, width, height, morphology.Radius, maximum);
                if (morphology.Kind is not MorphologyKind.Erosion and not MorphologyKind.Dilation)
                {
                    next = Extrema(next, width, height, morphology.Radius, !maximum);
                }
                if (morphology.Kind is MorphologyKind.BlackHat or MorphologyKind.WhiteTopHat)
                {
                    for (var i = 0; i < next.Length; i++)
                    {
                        next[i] = (float)(maximum ? next[i] - current[i] : current[i] - next[i]);
                    }
                }
                current = next;
            }
            else if (filter is Threshold threshold)
            {
                for (var i = 0; i < current.Length; i++)
                {
                    current[i] = double.IsFinite(current[i]) ? (current[i] > threshold.Level ? 255 : 0) : double.NaN;
                }
            }
            else
            {
                throw new ArgumentException("Missing test reference.");
            }
        }
        return current;
    }

    private static double[] Extrema(double[] input, int width, int height, int radius, bool maximum)
    {
        var output = new double[input.Length];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var best = maximum ? double.NegativeInfinity : double.PositiveInfinity;
                for (var dy = -radius; dy <= radius; dy++)
                {
                    for (var dx = -radius; dx <= radius; dx++)
                    {
                        var xx = Math.Min(width - 1, Math.Max(0, x + dx));
                        var yy = Math.Min(height - 1, Math.Max(0, y + dy));
                        var sample = input[(yy * width) + xx];
                        best = maximum ? Math.Max(sample, best) : Math.Min(sample, best);
                    }
                }
                output[(y * width) + x] = best;
            }
        }
        return output;
    }
}
