// File: src/VisionAlgorithm/OpenSrc/ShapeDetection/PrimitiveContourClassifier.Pcb.cs
using System.Collections.Immutable;
using VisionAlgorithm.OpenSrc.PatternMatching;
using VisionAlgorithm.OpenSrc.PatternMatching.Imaging;

namespace VisionAlgorithm.OpenSrc.ShapeDetection;

internal static partial class PrimitiveContourClassifier
{
    private static PrimitiveShapeDetection? ClassifyConnectedDoubleSquare(PrimitiveContour contour)
    {
        if (contour.HasHole || contour.Approximation.Length is < 6 or > 10 || Convex(contour.Approximation))
        {
            return null;
        }
        // 모서리가 맞닿아 한 윤곽이 된 두 사각형: 둘레의 이차 모멘트로 대각 방향을 구한다.
        var xx = 0d;
        var xy = 0d;
        var yy = 0d;
        for (var index = 0; index < contour.Points.Length; index++)
        {
            var a = Rotate(contour.Points[index], contour.Center, 0);
            var b = Rotate(contour.Points[(index + 1) % contour.Points.Length], contour.Center, 0);
            var length = Distance(a, b);
            xx += length * ((a.X * a.X) + (a.X * b.X) + (b.X * b.X)) / 3;
            yy += length * ((a.Y * a.Y) + (a.Y * b.Y) + (b.Y * b.Y)) / 3;
            xy += length * ((2 * a.X * a.Y) + (a.X * b.Y) + (b.X * a.Y) + (2 * b.X * b.Y)) / 6;
        }
        var angle = (.5 * Math.Atan2(2 * xy, xx - yy)) + (Math.PI / 4);
        var local = contour.Points.Select(point => Rotate(point, contour.Center, -angle)).ToArray();
        var side = (local.Max(p => p.X) - local.Min(p => p.X) + local.Max(p => p.Y) - local.Min(p => p.Y)) / 4;
        ImagePoint[] corners = [new(0, -side), new(side, -side), new(side, 0), new(0, 0),
            new(0, side), new(-side, side), new(-side, 0), new(0, 0)];
        var expected = corners.Select(point => ToWorld(point, contour.Center, angle)).ToImmutableArray();
        var score = PolygonScore(contour, expected, 2.6);
        return score < .8 ? null : new(PrimitiveShapeKind.DoubleSquare, contour.Center, angle, score, contour.Area, contour.Points);
    }

    private static bool EqualSides(ImmutableArray<ImagePoint> polygon)
    {
        var lengths = polygon.Select((point, index) => Distance(point, polygon[(index + 1) % polygon.Length])).ToArray();
        return lengths.Min() / lengths.Max() >= .88;
    }

    private static PrimitiveShapeDetection? ClassifyHash(PrimitiveContour contour, PrimitiveDetectionOptions options)
    {
        if (contour.Holes.Length != 1 || contour.Holes[0].Clipped || contour.FineApproximation.Length < 20)
        {
            return null;
        }
        var hole = Classify(contour.Holes[0], options with
        {
            Shapes = PrimitiveShapeKind.Rectangle
        });
        if (hole?.Kind != PrimitiveShapeKind.Rectangle)
        {
            return null;
        }
        var angle = HashAngle(contour);
        var result = ClassifyHashAtAngle(contour, hole, angle);
        if (result is not null)
        {
            return result;
        }
        // 짧은 픽셀 계단에서 얻은 초기 직선 각도만 보정한다. 매 자세에서 전체 윤곽/구멍 검증은 유지한다.
        Span<int> directions = [-1, 1];
        for (var step = 1; step <= 6; step++)
        {
            foreach (var direction in directions)
            {
                var candidate = ClassifyHashAtAngle(contour, hole, angle + (direction * step * Math.PI / 360));
                if (candidate is not null && (result is null || candidate.Score > result.Score))
                {
                    result = candidate;
                }
            }
        }
        return result;
    }

    private static PrimitiveShapeDetection? ClassifyHashAtAngle(PrimitiveContour contour, PrimitiveShapeDetection hole, double angle)
    {
        var local = contour.FineApproximation.Select(point => Rotate(point, contour.Center, -angle)).ToArray();
        var extent = local.Max(point => Math.Max(Math.Abs(point.X), Math.Abs(point.Y)));
        var slack = Math.Max(2.5, extent * .06);
        var xs = EdgeLevels(true);
        var ys = EdgeLevels(false);
        if (xs.Length != 6 || ys.Length != 6 || !SymmetricGrid(xs) || !SymmetricGrid(ys))
        {
            return null;
        }
        (int X, int Y)[] corners = [(1,0),(2,0),(2,1),(3,1),(3,0),(4,0),(4,1),(5,1),(5,2),(4,2),
            (4,3),(5,3),(5,4),(4,4),(4,5),(3,5),(3,4),(2,4),(2,5),(1,5),(1,4),(0,4),(0,3),
            (1,3),(1,2),(0,2),(0,1),(1,1)];
        var outer = corners.Select(index => ToWorld(new(xs[index.X], ys[index.Y]), contour.Center, angle)).ToImmutableArray();
        var inner = new[] { new ImagePoint(xs[2], ys[2]), new(xs[3], ys[2]), new(xs[3], ys[3]), new(xs[2], ys[3]) }
            .Select(point => ToWorld(point, contour.Center, angle)).ToImmutableArray();
        // OpenCV의 구멍 윤곽은 빈 픽셀 바깥의 전경을 따라 한 픽셀 확장된다.
        var innerScore = PolygonScore(contour.Holes[0], inner, 2, contour.Holes[0].Perimeter);
        var score = Math.Min(hole.Score, Math.Min(PolygonScore(contour, outer, 2.6), innerScore));
        return score < .8 ? null : new(PrimitiveShapeKind.CrossHash, contour.Center, angle, score,
            contour.Area - hole.AreaPixels, contour.Points)
        {
            InnerContours = [hole.Contour]
        };

        double[] EdgeLevels(bool vertical)
        {
            var coordinates = local.Select(point => vertical ? point.X : point.Y).ToArray();
            // 짧은 획 끝은 median 후 사선 두 개가 될 수 있다. 끝 위치도 격자 후보로 포함한다.
            var values = new List<double> { coordinates.Min(), coordinates.Max() };
            for (var index = 0; index < local.Length; index++)
            {
                var a = local[index];
                var b = local[(index + 1) % local.Length];
                var along = Math.Abs(vertical ? b.Y - a.Y : b.X - a.X);
                var across = Math.Abs(vertical ? b.X - a.X : b.Y - a.Y);
                if (along >= Math.Max(3, extent * .15) && across <= along * .25)
                {
                    values.Add(vertical ? (a.X + b.X) / 2 : (a.Y + b.Y) / 2);
                }
            }
            return Cluster(values, slack);
        }
    }

    private static bool SymmetricGrid(double[] values)
    {
        var widths = Enumerable.Range(0, 5).Select(index => values[index + 1] - values[index]).ToArray();
        return widths.All(value => value >= 2) && widths[1] / widths[3] is >= .8 and <= 1.25 &&
            widths[0] / widths[4] is >= .8 and <= 1.25 && widths[0] >= widths[1] * .5;
    }

    private static PrimitiveShapeDetection? ClassifyRadialMark(PrimitiveContour contour, PrimitiveDetectionOptions options)
    {
        if ((options.Shapes & (PrimitiveShapeKind.Star | PrimitiveShapeKind.Asterisk)) == 0)
        {
            return null;
        }
        ReadOnlySpan<ImmutableArray<ImagePoint>> approximations = [contour.Approximation, contour.FineApproximation];
        foreach (var approximation in approximations)
        {
            if (approximation.Length != 10 || (options.Shapes & PrimitiveShapeKind.Star) == 0)
            {
                continue;
            }
            var polygon = FitStraightEdges(contour.Points, approximation);
            var local = polygon.Select(point => new ImagePoint(point.X - contour.Center.X, point.Y - contour.Center.Y)).ToArray();
            var radii = local.Select(point => Distance(point, new(0, 0))).ToArray();
            if (polygon.Length == 10)
            {
                var offset = radii[0] > radii[1] ? 0 : 1;
                var tips = Enumerable.Range(0, 5).Select(index => local[(2 * index) + offset]).ToArray();
                var outerRadius = tips.Average(point => Distance(point, new(0, 0)));
                var innerRadius = Enumerable.Range(0, 5).Average(index => radii[(2 * index) + 1 - offset]);
                if (innerRadius / outerRadius is < .25 or > .7)
                {
                    continue;
                }
                var angle = PeriodicAngle(tips, 5);
                var expected = Enumerable.Range(0, 10).Select(index => Polar(contour.Center, angle + (index * Math.PI / 5),
                    index % 2 == 0 ? outerRadius : innerRadius)).ToImmutableArray();
                // 작은 별 끝의 median/픽셀 계단 오차도 양방향 윤곽 거리로 제한한다.
                var score = PolygonScore(contour, expected, 2);
                if (score >= .8)
                {
                    return new(PrimitiveShapeKind.Star, contour.Center, angle, score, contour.Area, contour.Points);
                }
            }
        }
        return (options.Shapes & PrimitiveShapeKind.Asterisk) != 0 ? ClassifyAsterisk(contour) : null;
    }

    private static PrimitiveShapeDetection? ClassifyAsterisk(PrimitiveContour contour)
    {
        var radius = contour.Points.Max(point => Distance(point, contour.Center));
        var samples = SampleContourPoints(contour);
        var groups = GroupAsteriskTips(samples, radius);
        if (groups.Count is not (6 or 8))
        {
            return null;
        }
        var rays = groups.Count;
        var angle = BoundaryAngle(contour, rays);
        var lengths = new List<double>();
        var widths = new List<double>();
        for (var ray = 0; ray < rays; ray++)
        {
            // 한 획의 방향은 모든 표본에 공통이다. 회전 계수는 한 번만 구하고,
            // 같은 순서로 길이와 폭을 수집해 중간 좌표 배열을 만들지 않는다.
            var direction = -angle - (ray * 2 * Math.PI / rays);
            var cosine = Math.Cos(direction);
            var sine = Math.Sin(direction);
            var length = double.NegativeInfinity;
            foreach (var point in samples)
            {
                var x = (cosine * point.X) - (sine * point.Y);
                var y = (sine * point.X) + (cosine * point.Y);
                length = Math.Max(length, x);
                if (x > radius * .5 && x < radius * .8 && Math.Abs(y) < radius * .3)
                {
                    widths.Add(Math.Abs(y));
                }
            }
            lengths.Add(length);
        }
        if (widths.Count == 0)
        {
            return null;
        }
        radius = lengths.Average();
        widths.Sort();
        var halfWidth = widths[widths.Count / 2];
        if (halfWidth / radius is < .08 or > .25)
        {
            return null;
        }
        var expected = ImmutableArray.CreateBuilder<ImagePoint>(rays * 3);
        for (var ray = 0; ray < rays; ray++)
        {
            var direction = angle + (ray * 2 * Math.PI / rays);
            expected.Add(ToWorld(new(radius, -halfWidth), contour.Center, direction));
            expected.Add(ToWorld(new(radius, halfWidth), contour.Center, direction));
            expected.Add(Polar(contour.Center, direction + (Math.PI / rays), halfWidth / Math.Sin(Math.PI / rays)));
        }
        // 3×3 median과 픽셀 양자화로 45° 오목 교점이 약3px 둥글어진다.
        var score = PolygonScore(contour, expected.MoveToImmutable(), 3.2);
        return score < .8 ? null : new(PrimitiveShapeKind.Asterisk, contour.Center, angle, score, contour.Area, contour.Points);
    }

    private static List<ImagePoint> SampleContourPoints(PrimitiveContour contour)
    {
        var samples = new List<ImagePoint>();
        for (var index = 0; index < contour.Points.Length; index++)
        {
            var a = contour.Points[index];
            var b = contour.Points[(index + 1) % contour.Points.Length];
            var count = Math.Max(1, (int)Math.Ceiling(Distance(a, b)));
            for (var sample = 0; sample < count; sample++)
            {
                samples.Add(new(a.X + ((b.X - a.X) * sample / count) - contour.Center.X,
                    a.Y + ((b.Y - a.Y) * sample / count) - contour.Center.Y));
            }
        }
        return samples;
    }

    private static List<List<ImagePoint>> GroupAsteriskTips(List<ImagePoint> samples, double radius)
    {
        var tips = samples.Where(point => Distance(point, new(0, 0)) >= radius * .9)
            .OrderBy(point => Math.Atan2(point.Y, point.X)).ToArray();
        var groups = new List<List<ImagePoint>>();
        foreach (var point in tips)
        {
            if (groups.Count == 0 || Math.Atan2(point.Y, point.X) - Math.Atan2(groups[^1][^1].Y, groups[^1][^1].X) > .4)
            {
                groups.Add([]);
            }
            groups[^1].Add(point);
        }
        if (groups.Count > 1 && Math.Atan2(tips[0].Y, tips[0].X) + (2 * Math.PI) - Math.Atan2(tips[^1].Y, tips[^1].X) <= .4)
        {
            groups[0].AddRange(groups[^1]);
            groups.RemoveAt(groups.Count - 1);
        }
        return groups;
    }

    private static double PeriodicAngle(ImagePoint[] points, int period) => Math.Atan2(
        points.Sum(point => Math.Sin(period * Math.Atan2(point.Y, point.X))),
        points.Sum(point => Math.Cos(period * Math.Atan2(point.Y, point.X)))) / period;

    private static double HashAngle(PrimitiveContour contour)
    {
        var initial = BoundaryAngle(contour, 4);
        var real = 0d;
        var imaginary = 0d;
        var magnitude = 0d;
        // z^4의 둘레 적분은 회전에 따라 4배의 위상으로 변한다. 3점 Gauss로 각 선분을 정확히 적분한다.
        // 축/대각선의 45° 모호성은 기존 직선 방향으로 해소한다. 짧은 획의 계단 오차를 평균화한다.
        foreach (var (point, index) in contour.Points.Select((point, index) => (point, index)))
        {
            var a = Rotate(point, contour.Center, 0);
            var b = Rotate(contour.Points[(index + 1) % contour.Points.Length], contour.Center, 0);
            var length = Distance(a, b);
            for (var sample = -1; sample <= 1; sample++)
            {
                var fraction = .5 + (sample * Math.Sqrt(.6) / 2);
                var x = a.X + ((b.X - a.X) * fraction);
                var y = a.Y + ((b.Y - a.Y) * fraction);
                var weight = length * (sample == 0 ? 8d / 9 : 5d / 9) / 2;
                real += weight * ((x * x * x * x) - (6 * x * x * y * y) + (y * y * y * y));
                imaginary += weight * 4 * x * y * ((x * x) - (y * y));
                magnitude += weight * Math.Pow((x * x) + (y * y), 2);
            }
        }
        if (Math.Sqrt((real * real) + (imaginary * imaginary)) < magnitude * .05)
        {
            return initial;
        }
        var measured = Math.Atan2(imaginary, real) / 4;
        return initial + Math.IEEERemainder(measured - initial, Math.PI / 4);
    }

    private static double BoundaryAngle(PrimitiveContour contour, int period)
    {
        var sine = 0d;
        var cosine = 0d;
        var points = contour.FineApproximation;
        for (var index = 0; index < points.Length; index++)
        {
            var a = points[index];
            var b = points[(index + 1) % points.Length];
            var length = Distance(a, b);
            if (length >= Math.Sqrt(contour.Area) * .15)
            {
                var samples = SampleInteriorEdgePoints(contour.Points, a, b, length);
                if (samples.Count < 3)
                {
                    continue;
                }
                var x = samples.Average(point => point.X);
                var y = samples.Average(point => point.Y);
                var xx = samples.Sum(point => (point.X - x) * (point.X - x));
                var xy = samples.Sum(point => (point.X - x) * (point.Y - y));
                var yy = samples.Sum(point => (point.Y - y) * (point.Y - y));
                var direction = .5 * Math.Atan2(2 * xy, xx - yy);
                sine += length * Math.Sin(period * direction);
                cosine += length * Math.Cos(period * direction);
            }
        }
        return Math.Atan2(sine, cosine) / period;
    }

    private static List<ImagePoint> SampleInteriorEdgePoints(ImmutableArray<ImagePoint> contourPoints,
        ImagePoint a, ImagePoint b, double length)
    {
        var samples = new List<ImagePoint>();
        var end = contourPoints.IndexOf(b);
        for (var edge = contourPoints.IndexOf(a); edge != end; edge = (edge + 1) % contourPoints.Length)
        {
            var first = contourPoints[edge];
            var last = contourPoints[(edge + 1) % contourPoints.Length];
            var count = Math.Max(1, (int)Math.Ceiling(Distance(first, last)));
            for (var sample = 0; sample < count; sample++)
            {
                var point = new ImagePoint(first.X + ((last.X - first.X) * sample / count),
                    first.Y + ((last.Y - first.Y) * sample / count));
                var fraction = (((point.X - a.X) * (b.X - a.X)) + ((point.Y - a.Y) * (b.Y - a.Y))) / (length * length);
                if (fraction is >= .1 and <= .9)
                {
                    samples.Add(point);
                }
            }
        }
        return samples;
    }

    private static ImagePoint Polar(ImagePoint center, double angle, double radius)
        => new(center.X + (radius * Math.Cos(angle)), center.Y + (radius * Math.Sin(angle)));

    private static ImagePoint Rotate(ImagePoint point, ImagePoint origin, double angle)
        => new((Math.Cos(angle) * (point.X - origin.X)) - (Math.Sin(angle) * (point.Y - origin.Y)),
            (Math.Sin(angle) * (point.X - origin.X)) + (Math.Cos(angle) * (point.Y - origin.Y)));

    private static ImagePoint ToWorld(ImagePoint point, ImagePoint origin, double angle)
    {
        var rotated = Rotate(point, new(0, 0), angle);
        return new(rotated.X + origin.X, rotated.Y + origin.Y);
    }

    private static double PolygonScore(PrimitiveContour contour, ImmutableArray<ImagePoint> expected, double pixelSlack,
        double pixelAreaAllowance = 0)
    {
        var area = Area(expected);
        var fit = 1 - (Math.Max(0, Math.Abs(area - contour.Area) - pixelAreaAllowance) / Math.Max(area, contour.Area));
        if (fit < .88)
        {
            return 0;
        }
        var size = Math.Sqrt(area);
        var slack = Math.Max(pixelSlack, size * .035);
        return Close(contour.Points, expected) && Close(expected, contour.Points) ? fit : 0;

        bool Close(ImmutableArray<ImagePoint> source, ImmutableArray<ImagePoint> target)
        {
            var previousEdge = 0;
            for (var index = 0; index < source.Length; index++)
            {
                var first = source[index];
                var last = source[(index + 1) % source.Length];
                for (var sample = 0; sample < 3; sample++)
                {
                    var point = new ImagePoint(first.X + ((last.X - first.X) * sample / 3),
                        first.Y + ((last.Y - first.Y) * sample / 3));
                    // 이웃 표본은 같은 변에 가까운 경우가 많다. 이전 변부터 확인하되,
                    // 실패하면 모든 변을 그대로 검사한다. 표본/거리/허용치는 바꾸지 않는다.
                    if (SegmentDistance(point, target[previousEdge], target[(previousEdge + 1) % target.Length]) <= slack)
                    {
                        continue;
                    }
                    var matched = false;
                    for (var edge = 0; edge < target.Length; edge++)
                    {
                        if (SegmentDistance(point, target[edge], target[(edge + 1) % target.Length]) <= slack)
                        {
                            matched = true;
                            previousEdge = edge;
                            break;
                        }
                    }
                    if (!matched)
                    {
                        return false;
                    }
                }
            }
            return true;
        }
    }
}
