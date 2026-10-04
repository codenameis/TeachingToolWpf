using System.Diagnostics;
using VisionAlgorithm.OpenSrc.PatternMatching;

namespace VisionAlgorithm.OpenSrc.Comparisons;

public sealed record ChamferComparisonResult(MatchResult Result, int SceneLines, long EvaluatedHypotheses,
    long SkippedHypotheses, double LineExtractionMilliseconds, double DistanceMapMilliseconds);

/// <summary>E1 논문 기반 8방향 격자 비교판. 선분 추출·방향 거리·선분 적분·안전한 이동 하한을 포함한다.</summary>
public static class DirectionalChamferMatcher
{
    private readonly record struct RasterLine(int X, int Y, int Bin, int Count);

    public static LineShapeModel Train(GrayImage image, TrainingRegion region, TrainingOptions options, CancellationToken token = default)
        => LineSegments.Train(image, region, options, token);

    public static ChamferComparisonResult Search(GrayImage image, LineShapeModel model, SearchOptions options,
        double maximumMeanDistance = 2, bool skipRegions = true, CancellationToken token = default)
    {
        options.Validate();
        if (!double.IsFinite(maximumMeanDistance) || maximumMeanDistance <= 0 || (long)image.Width * image.Height > 262144)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumMeanDistance), "FDCM 비교판은 256 Kpx 이하 입력을 사용합니다.");
        }
        var prepared = PreparedSearchImage.Create(image, model.Shape.Options.MinimumGradient, model.Shape.Levels.Length, token);
        var timer = Stopwatch.StartNew();
        var lines = LineSegments.Extract(prepared.Gradients[0], token);
        var extraction = timer.Elapsed.TotalMilliseconds;
        timer.Restart();
        var map = new DirectionalDistanceMap(image.Width, image.Height, lines, 5, token);
        var mapTime = timer.Elapsed.TotalMilliseconds;
        timer.Restart();
        var queue = new PriorityQueue<PoseCandidate, double>();
        var budget = false;
        long evaluated = 0;
        long skipped = 0;
        for (var ti = 0; ti < model.Shape.Templates.Length; ti++)
        {
            token.ThrowIfCancellationRequested();
            var template = model.Shape.Templates[ti];
            var transformed = model.Levels[0].Select(l => Rasterize(l, template.Angle, template.Scale)).OrderByDescending(l => l.Count).ToArray();
            var points = transformed.Sum(l => l.Count);
            var cells = new Dictionary<(int X, int Y), PoseCandidate>();
            for (var y = 0; y < image.Height; y++)
            {
                token.ThrowIfCancellationRequested();
                for (var x = 0; x < image.Width;)
                {
                    evaluated++;
                    var sum = 0d;
                    foreach (var line in transformed)
                    {
                        sum += map.Sum(line.Bin, x + line.X, y + line.Y, line.Count);
                        if (!skipRegions && sum > maximumMeanDistance * points)
                        {
                            break;
                        }
                    }
                    var cost = sum / points;
                    if (cost <= maximumMeanDistance)
                    {
                        var candidate = new PoseCandidate(new(x + 0.5, y + 0.5, template.Angle, template.Scale), Math.Exp(-cost), ti);
                        var key = (x / 4, y / 4);
                        if (!cells.TryGetValue(key, out var previous) || candidate.Score > previous.Score)
                        {
                            cells[key] = candidate;
                        }
                    }
                    // 식 13의 1-Lipschitz 하한. 부분 합은 이 건너뛰기에 사용하지 않는다.
                    var step = skipRegions && double.IsFinite(cost) ? Math.Max(1, (int)Math.Min(image.Width, Math.Floor(cost - maximumMeanDistance))) : 1;
                    skipped += Math.Min(step, image.Width - x) - 1;
                    x += step;
                }
            }
            foreach (var candidate in cells.Values)
            {
                queue.Enqueue(candidate, candidate.Score);
                if (queue.Count > options.MaximumCandidates)
                {
                    queue.Dequeue();
                    budget = true;
                }
            }
        }
        var candidates = queue.UnorderedItems.Select(p => p.Element).OrderByDescending(p => p.Score).ToList();
        var result = ShapeMatcher.CompleteCandidates(prepared, model.Shape, options, new(candidates, budget, model.Shape.TemplateCount),
            extraction + mapTime + timer.Elapsed.TotalMilliseconds, token);
        result = result with
        {
            Diagnostics = result.Diagnostics with
            {
                SearchPolicy = "E1 8격자 방향 비교판: 거리+방향 비용, 선분 prefix, 이동 하한. 원문의 60 균등 방향/RANSAC와 다름."
            }
        };
        return new(result, lines.Length, evaluated, skipped, extraction, mapTime);
    }

    private static RasterLine Rasterize(LineSegment line, double angle, double scale)
    {
        var cos = Math.Cos(angle) * scale;
        var sin = Math.Sin(angle) * scale;
        var a = new ImagePoint((cos * line.Start.X) - (sin * line.Start.Y), (sin * line.Start.X) + (cos * line.Start.Y));
        var b = new ImagePoint((cos * line.End.X) - (sin * line.End.Y), (sin * line.End.X) + (cos * line.End.Y));
        var bin = DirectionalDistanceMap.Bin(line.Angle + angle);
        var direction = DirectionalDistanceMap.s_directions[bin];
        if (((b.X - a.X) * direction.X) + ((b.Y - a.Y) * direction.Y) < 0)
        {
            (a, b) = (b, a);
        }
        var spacing = Math.Sqrt((direction.X * direction.X) + (direction.Y * direction.Y));
        var count = Math.Max(1, (int)Math.Round(line.Length * scale / spacing) + 1);
        var center = new ImagePoint((a.X + b.X) / 2, (a.Y + b.Y) / 2);
        return new((int)Math.Round(center.X - ((count - 1) * direction.X / 2d)),
            (int)Math.Round(center.Y - ((count - 1) * direction.Y / 2d)), bin, count);
    }
}
