using System.Collections.Immutable;
using VisionAlgorithm.OpenSrc.PatternMatching;

namespace VisionAlgorithm.OpenSrc.Comparisons;

public readonly record struct LineSegment(ImagePoint Start, ImagePoint End)
{
    public ImagePoint Middle => new((Start.X + End.X) / 2, (Start.Y + End.Y) / 2);
    public double Length => Math.Sqrt(((End.X - Start.X) * (End.X - Start.X)) + ((End.Y - Start.Y) * (End.Y - Start.Y)));
    public double Angle => Math.Atan2(End.Y - Start.Y, End.X - Start.X);
}

public sealed record LineShapeModel(ShapeModel Shape, ImmutableArray<ImmutableArray<LineSegment>> Levels);

internal static class LineSegments
{
    public static LineShapeModel Train(GrayImage image, TrainingRegion region, TrainingOptions options, CancellationToken token)
    {
        var shape = ShapeTrainer.Train(image, region, options, token);
        var levels = ImmutableArray.CreateBuilder<ImmutableArray<LineSegment>>();
        var current = image;
        var cos = Math.Cos(region.ReferenceAngle);
        var sin = Math.Sin(region.ReferenceAngle);
        for (var level = 0; level < Math.Min(2, shape.Levels.Length); level++)
        {
            var factor = 1 << level;
            var gradient = GradientImage.Create(current, options.MinimumGradient, token);
            var lines = Extract(gradient, token).Where(line => region.Mask.IsEmpty ||
                Enumerable.Range(0, Math.Max(2, (int)line.Length)).All(i =>
                {
                    var t = i / (double)(Math.Max(2, (int)line.Length) - 1);
                    var x = Math.Clamp((int)((line.Start.X + (t * (line.End.X - line.Start.X))) * factor), 0, image.Width - 1);
                    var y = Math.Clamp((int)((line.Start.Y + (t * (line.End.Y - line.Start.Y))) * factor), 0, image.Height - 1);
                    return region.Mask.Span[(y * image.Width) + x] != 0;
                })).Select(line =>
                {
                    ImagePoint Local(ImagePoint p)
                    {
                        var x = (p.X * factor) - region.ReferencePoint.X;
                        var y = (p.Y * factor) - region.ReferencePoint.Y;
                        return new((cos * x) + (sin * y), (-sin * x) + (cos * y));
                    }
                    return new LineSegment(Local(line.Start), Local(line.End));
                }).ToImmutableArray();
            levels.Add(lines);
            current = GradientImage.Reduce(current);
        }
        if (levels[0].Length < 3)
        {
            throw new ArgumentException("선분 비교 경로에는 길이 5px 이상의 선분이 세 개 이상 필요합니다.");
        }
        return new(shape, levels.ToImmutable());
    }

    public static ImmutableArray<LineSegment> Extract(GradientImage image, CancellationToken token)
    {
        var visited = new bool[image.Width * image.Height];
        var output = ImmutableArray.CreateBuilder<LineSegment>();
        for (var sy = 0; sy < image.Height; sy++)
        {
            token.ThrowIfCancellationRequested();
            for (var sx = 0; sx < image.Width; sx++)
            {
                var index = (sy * image.Width) + sx;
                if (visited[index] || image.EdgeAt(sx, sy) is not { } seed)
                {
                    continue;
                }
                var pending = new Queue<(int X, int Y)>();
                var points = new List<EdgePoint>();
                pending.Enqueue((sx, sy));
                visited[index] = true;
                var visitedCount = 0;
                while (pending.TryDequeue(out var point))
                {
                    if ((visitedCount++ & 255) == 0)
                    {
                        token.ThrowIfCancellationRequested();
                    }

                    var edge = image.EdgeAt(point.X, point.Y)!.Value;
                    points.Add(edge);
                    for (var dy = -2; dy <= 2; dy++)
                    {
                        for (var dx = -2; dx <= 2; dx++)
                        {
                            var x = point.X + dx;
                            var y = point.Y + dy;
                            if (x < 0 || y < 0 || x >= image.Width || y >= image.Height || visited[(y * image.Width) + x] ||
                                image.EdgeAt(x, y) is not { } neighbor)
                            {
                                continue;
                            }
                            var dot = (seed.NormalX * neighbor.NormalX) + (seed.NormalY * neighbor.NormalY);
                            var distance = Math.Abs(((neighbor.X - seed.X) * seed.NormalX) + ((neighbor.Y - seed.Y) * seed.NormalY));
                            if (dot >= 0.96 && distance <= 0.8)
                            {
                                visited[(y * image.Width) + x] = true;
                                pending.Enqueue((x, y));
                            }
                        }
                    }
                }
                if (points.Count < 6)
                {
                    continue;
                }
                var mx = points.Average(p => p.X);
                var my = points.Average(p => p.Y);
                var xx = points.Sum(p => (p.X - mx) * (p.X - mx));
                var yy = points.Sum(p => (p.Y - my) * (p.Y - my));
                var xy = points.Sum(p => (p.X - mx) * (p.Y - my));
                var angle = 0.5 * Math.Atan2(2 * xy, xx - yy);
                var tx = Math.Cos(angle);
                var ty = Math.Sin(angle);
                if ((tx * seed.NormalY) - (ty * seed.NormalX) < 0)
                {
                    tx = -tx;
                    ty = -ty;
                }
                var positions = points.Select(p => ((p.X - mx) * tx) + ((p.Y - my) * ty)).ToArray();
                var a = positions.Min();
                var b = positions.Max();
                if (b - a >= 5)
                {
                    output.Add(new(new(mx + (tx * a), my + (ty * a)), new(mx + (tx * b), my + (ty * b))));
                }
                if (output.Count > 1024)
                {
                    throw new InvalidOperationException("비교 경로의 선분 예산 1024개 초과. 입력을 나누십시오.");
                }
            }
        }
        return output.ToImmutable();
    }
}
