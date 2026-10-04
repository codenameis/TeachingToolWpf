using System.Collections.Immutable;
using System.Diagnostics;
using VisionAlgorithm.OpenSrc.PatternMatching;

namespace VisionAlgorithm.OpenSrc.Comparisons;

public sealed class GrayFusionModel
{
    public ShapeModel Shape
    {
        get;
    }
    internal ImmutableArray<(double X, double Y, double Value)> Samples
    {
        get;
    }
    internal int Factor
    {
        get;
    }
    internal double Energy
    {
        get;
    }

    internal GrayFusionModel(ShapeModel shape, ImmutableArray<(double X, double Y, double Value)> samples, int factor)
    {
        Shape = shape;
        Samples = samples;
        Factor = factor;
        Energy = samples.Sum(s => s.Value * s.Value);
    }
}

public sealed record FusionComparisonResult(MatchResult Result, int GrayCandidates, bool FallbackUsed, double FallbackMilliseconds);

/// <summary>E2 §2–4 착안 비교 경로. 상위 일반 masked NCC + 하위 연속 방향/정밀화. Winograd FNM 재현이 아니다.</summary>
public static class GrayGradientFusion
{
    public static GrayFusionModel Train(GrayImage image, TrainingRegion region, TrainingOptions options, CancellationToken token = default)
    {
        var shape = ShapeTrainer.Train(image, region, options, token);
        var factor = shape.Levels[^1].Factor;
        var coarse = image;
        for (var level = 1; level < shape.Levels.Length; level++)
        {
            coarse = GradientImage.Reduce(coarse);
        }
        var samples = new List<(double X, double Y, double Value)>();
        var cos = Math.Cos(region.ReferenceAngle);
        var sin = Math.Sin(region.ReferenceAngle);
        for (var y = 0; y < coarse.Height; y++)
        {
            token.ThrowIfCancellationRequested();
            for (var x = 0; x < coarse.Width; x++)
            {
                var sx = Math.Min(image.Width - 1, (int)((x + 0.5) * factor));
                var sy = Math.Min(image.Height - 1, (int)((y + 0.5) * factor));
                if (!region.Mask.IsEmpty && region.Mask.Span[(sy * image.Width) + sx] == 0)
                {
                    continue;
                }
                var dx = x + 0.5 - (region.ReferencePoint.X / factor);
                var dy = y + 0.5 - (region.ReferencePoint.Y / factor);
                samples.Add(((cos * dx) + (sin * dy), (-sin * dx) + (cos * dy), coarse.Pixels.Span[(y * coarse.Stride) + x]));
            }
        }
        var mean = samples.Average(s => s.Value);
        var centered = samples.Select(s => (s.X, s.Y, s.Value - mean)).ToImmutableArray();
        return new(shape, centered, factor);
    }

    public static FusionComparisonResult Search(GrayImage image, GrayFusionModel model, SearchOptions options,
        double minimumNcc = 0.6, bool fallbackWhenEmpty = false, CancellationToken token = default)
    {
        options.Validate();
        if (!double.IsFinite(minimumNcc) || minimumNcc is < -1 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumNcc));
        }
        var total = Stopwatch.StartNew();
        var prepared = PreparedSearchImage.Create(image, model.Shape.Options.MinimumGradient, model.Shape.Levels.Length, token);
        var timer = Stopwatch.StartNew();
        var coarse = image;
        for (var level = 1; level < model.Shape.Levels.Length; level++)
        {
            coarse = GradientImage.Reduce(coarse);
        }
        var values = new float[coarse.Width * coarse.Height];
        for (var y = 0; y < coarse.Height; y++)
        {
            for (var x = 0; x < coarse.Width; x++)
            {
                values[(y * coarse.Width) + x] = coarse.Pixels.Span[(y * coarse.Stride) + x];
            }
        }
        var queue = new PriorityQueue<PoseCandidate, double>();
        var budget = false;
        for (var ti = 0; ti < model.Shape.Templates.Length; ti++)
        {
            var template = model.Shape.Templates[ti];
            var cos = Math.Cos(template.Angle) * template.Scale;
            var sin = Math.Sin(template.Angle) * template.Scale;
            var samples = model.Samples.Select(s => (X: (cos * s.X) - (sin * s.Y), Y: (sin * s.X) + (cos * s.Y), s.Value)).ToArray();
            var scores = new double[values.Length];
            for (var y = 0; y < coarse.Height; y++)
            {
                token.ThrowIfCancellationRequested();
                for (var x = 0; x < coarse.Width; x++)
                {
                    var sum = 0d;
                    var squared = 0d;
                    var product = 0d;
                    foreach (var sample in samples)
                    {
                        var value = GradientImage.Sample(values, coarse.Width, coarse.Height, x + sample.X, y + sample.Y);
                        sum += value;
                        squared += value * value;
                        product += value * sample.Value;
                    }
                    var variance = squared - (sum * sum / samples.Length);
                    scores[(y * coarse.Width) + x] = variance > 1e-9 ? product / Math.Sqrt(variance * model.Energy) : -1;
                }
            }
            for (var y = 1; y < coarse.Height - 1; y++)
            {
                for (var x = 1; x < coarse.Width - 1; x++)
                {
                    var i = (y * coarse.Width) + x;
                    var score = scores[i];
                    if (score < minimumNcc || score < scores[i - 1] || score <= scores[i + 1] ||
                        score < scores[i - coarse.Width] || score <= scores[i + coarse.Width])
                    {
                        continue;
                    }
                    queue.Enqueue(new(new((x + 0.5) * model.Factor, (y + 0.5) * model.Factor, template.Angle, template.Scale), score, ti), score);
                    if (queue.Count > options.MaximumCandidates)
                    {
                        queue.Dequeue();
                        budget = true;
                    }
                }
            }
        }
        var candidates = queue.UnorderedItems.Select(p => p.Element).OrderByDescending(p => p.Score).ToList();
        var result = ShapeMatcher.CompleteCandidates(prepared, model.Shape, options, new(candidates, budget, model.Shape.TemplateCount), timer.Elapsed.TotalMilliseconds, token);
        var fallbackTime = 0d;
        var fallback = fallbackWhenEmpty && result.Matches.IsEmpty;
        if (fallback)
        {
            timer.Restart();
            result = new ShapeMatcher().Search(prepared, model.Shape, options, token);
            fallbackTime = timer.Elapsed.TotalMilliseconds;
        }
        result = result with
        {
            Diagnostics = result.Diagnostics with
            {
                SearchPolicy = $"E2 착안: 상위 일반 NCC({minimumNcc:F2}) → 방향 검증. Winograd 미적용. fallback {fallback}. 전체 {total.Elapsed.TotalMilliseconds:F2} ms"
            }
        };
        return new(result, candidates.Count, fallback, fallbackTime);
    }
}
