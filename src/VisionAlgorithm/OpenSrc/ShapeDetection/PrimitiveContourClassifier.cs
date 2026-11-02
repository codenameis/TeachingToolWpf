using System.Collections.Immutable;
using VisionAlgorithm.OpenSrc.PatternMatching;
using VisionAlgorithm.OpenSrc.PatternMatching.Imaging;

namespace VisionAlgorithm.OpenSrc.ShapeDetection;

internal static partial class PrimitiveContourClassifier
{
    public static PrimitiveShapeDetection? Classify(PrimitiveContour contour, PrimitiveDetectionOptions options)
    {
        if (contour.HasHole)
        {
            return ((options.Shapes & PrimitiveShapeKind.CrossHash) != 0 ? ClassifyHash(contour, options) : null) ?? ClassifyFrame(contour, options);
        }
        var polygon = contour.Approximation;
        if (polygon.Length < 3)
        {
            return null;
        }
        if (polygon.Length is 4 or 12)
        {
            polygon = FitStraightEdges(contour.Points, polygon);
        }
        var convex = Convex(polygon);
        var areaFit = Math.Min(contour.Area, Area(polygon)) / Math.Max(contour.Area, Area(polygon));
        var angle = Math.Atan2(polygon[1].Y - polygon[0].Y, polygon[1].X - polygon[0].X);
        if (polygon.Length == 3 && convex && areaFit >= .9)
        {
            return Shape(PrimitiveShapeKind.Triangle, areaFit);
        }
        if (polygon.Length == 4 && convex && Orthogonal(polygon, .25) && areaFit >= .9)
        {
            angle = OrthogonalAngle(polygon);
            if ((options.Shapes & PrimitiveShapeKind.Rectangle) == 0 && (options.Shapes & PrimitiveShapeKind.Diamond) != 0 && EqualSides(polygon))
            {
                return Shape(PrimitiveShapeKind.Diamond, areaFit);
            }
            return Shape(PrimitiveShapeKind.Rectangle, areaFit);
        }
        if (polygon.Length == 4 && convex && EqualSides(polygon) && areaFit >= .9 &&
            (options.Shapes & PrimitiveShapeKind.Diamond) != 0)
        {
            var diagonal = Distance(polygon[0], polygon[2]) >= Distance(polygon[1], polygon[3]) ? 0 : 1;
            angle = Math.Atan2(polygon[diagonal + 2].Y - polygon[diagonal].Y, polygon[diagonal + 2].X - polygon[diagonal].X);
            return Shape(PrimitiveShapeKind.Diamond, areaFit);
        }
        if (polygon.Length == 12 && !convex && Orthogonal(polygon, .25) && areaFit >= .88)
        {
            angle = OrthogonalAngle(polygon);
            if (IsCross(polygon, angle, options.ApproximationTolerance))
            {
                angle = CrossAngle(contour, angle);
                return Shape(PrimitiveShapeKind.Cross, areaFit);
            }
        }
        if (polygon.Length >= 8 && convex && TryClassifyCircle(contour, out var circle))
        {
            return circle;
        }
        if (convex && polygon.Length is >= 5 and <= 8)
        {
            return ClassifyBeveledRectangle(contour);
        }
        return ((options.Shapes & PrimitiveShapeKind.DoubleSquare) != 0 ? ClassifyConnectedDoubleSquare(contour) : null)
            ?? ClassifyRadialMark(contour, options);

        PrimitiveShapeDetection Shape(PrimitiveShapeKind kind, double score)
            => new(kind, contour.Center, angle, Math.Clamp(score, 0, 1), contour.Area, contour.Points);
    }

    private static bool TryClassifyCircle(PrimitiveContour contour, out PrimitiveShapeDetection? circle)
    {
        var radii = BoundaryRadii(contour.Points, contour.Center).ToArray();
        var radius = radii.Average();
        var radialError = Math.Sqrt(radii.Average(value => (value - radius) * (value - radius))) / radius;
        var circularity = 4 * Math.PI * contour.Area / (contour.Perimeter * contour.Perimeter);
        if (radialError <= .05 && radii.Min() / radii.Max() >= .85 && circularity >= .8)
        {
            circle = new(PrimitiveShapeKind.Circle, contour.Center, 0, Math.Clamp(1 - (2 * radialError), 0, 1), contour.Area, contour.Points);
            return true;
        }
        circle = null;
        return false;
    }

    private static PrimitiveShapeDetection? ClassifyFrame(PrimitiveContour contour, PrimitiveDetectionOptions options)
    {
        if (contour.Holes.Length != 1 || contour.Holes[0].Clipped)
        {
            return null;
        }
        var outer = Classify(contour with
        {
            HasHole = false,
            Holes = []
        }, options with
        {
            Shapes = PrimitiveShapeKind.Rectangle | PrimitiveShapeKind.Circle
        });
        var inner = Classify(contour.Holes[0], options with
        {
            Shapes = PrimitiveShapeKind.Rectangle | PrimitiveShapeKind.Circle
        });
        if (outer is null || inner is null || outer.Kind != inner.Kind || inner.AreaPixels >= outer.AreaPixels)
        {
            return null;
        }
        var kind = outer.Kind switch
        {
            PrimitiveShapeKind.Circle => PrimitiveShapeKind.Ring,
            PrimitiveShapeKind.Rectangle => PrimitiveShapeKind.RectangleFrame,
            _ => PrimitiveShapeKind.None
        };
        return kind == PrimitiveShapeKind.None ? null : outer with
        {
            Kind = kind,
            Score = Math.Min(outer.Score, inner.Score),
            AreaPixels = outer.AreaPixels - inner.AreaPixels,
            InnerContours = [inner.Contour]
        };
    }

    private static PrimitiveShapeDetection? ClassifyBeveledRectangle(PrimitiveContour contour)
    {
        if (contour.Approximation.Length is < 5 or > 8)
        {
            return null;
        }
        // 작은 사각형과 구멍 윤곽의 픽셀 계단/median으로 생긴
        // 짧은 모서리 대신 네 긴 변을 맞추되 직각·면적·전체 윤곽 거리를 모두 검증한다.
        var rectangle = FitStraightEdges(contour.Points, contour.Approximation, true);
        var areaFit = Math.Min(contour.Area, Area(rectangle)) / Math.Max(contour.Area, Area(rectangle));
        if (rectangle.Length != 4 || !Convex(rectangle) || !Orthogonal(rectangle, .25) || areaFit < .9 ||
            contour.Points.Any(point => Enumerable.Range(0, 4).Min(edge =>
                SegmentDistance(point, rectangle[edge], rectangle[(edge + 1) % 4])) > 2))
        {
            return null;
        }
        return new(PrimitiveShapeKind.Rectangle, contour.Center, OrthogonalAngle(rectangle),
            areaFit, contour.Area, contour.Points);
    }

    private static double SegmentDistance(ImagePoint point, ImagePoint first, ImagePoint last)
    {
        var dx = last.X - first.X;
        var dy = last.Y - first.Y;
        var lengthSquared = (dx * dx) + (dy * dy);
        var fraction = lengthSquared == 0 ? 0 : Math.Clamp((((point.X - first.X) * dx) + ((point.Y - first.Y) * dy)) / lengthSquared, 0, 1);
        return Distance(point, new(first.X + (fraction * dx), first.Y + (fraction * dy)));
    }

    private static bool IsCross(ImmutableArray<ImagePoint> polygon, double angle, double tolerance)
    {
        var cosine = Math.Cos(angle);
        var sine = Math.Sin(angle);
        var points = polygon.Select(point => new ImagePoint((cosine * point.X) + (sine * point.Y),
            (-sine * point.X) + (cosine * point.Y))).ToArray();
        var size = Math.Max(points.Max(p => p.X) - points.Min(p => p.X), points.Max(p => p.Y) - points.Min(p => p.Y));
        var slack = Math.Max(2, size * Math.Min(.06, tolerance * 2));
        var xs = Cluster(points.Select(p => p.X), slack);
        var ys = Cluster(points.Select(p => p.Y), slack);
        if (xs.Length != 4 || ys.Length != 4)
        {
            return false;
        }
        var horizontalArm = xs[1] - xs[0];
        var oppositeHorizontal = xs[3] - xs[2];
        var verticalArm = ys[1] - ys[0];
        var oppositeVertical = ys[3] - ys[2];
        if (Math.Min(horizontalArm, oppositeHorizontal) / Math.Max(horizontalArm, oppositeHorizontal) < .75 ||
            Math.Min(verticalArm, oppositeVertical) / Math.Max(verticalArm, oppositeVertical) < .75 ||
            xs[2] - xs[1] > .6 * (xs[3] - xs[0]) || ys[2] - ys[1] > .6 * (ys[3] - ys[0]))
        {
            return false;
        }
        ImagePoint[] expected = [new(xs[1], ys[0]), new(xs[2], ys[0]), new(xs[2], ys[1]), new(xs[3], ys[1]),
            new(xs[3], ys[2]), new(xs[2], ys[2]), new(xs[2], ys[3]), new(xs[1], ys[3]),
            new(xs[1], ys[2]), new(xs[0], ys[2]), new(xs[0], ys[1]), new(xs[1], ys[1])];
        return expected.All(vertex => points.Count(point => Distance(point, vertex) <= slack) == 1);
    }

    private static IEnumerable<double> BoundaryRadii(ImmutableArray<ImagePoint> contour, ImagePoint center)
    {
        // ApproxSimple은 직선 중간점을 없앤다. 꼭짓점만 재면 작은 사각형도 반경이 일정해 보인다.
        for (var index = 0; index < contour.Length; index++)
        {
            var first = contour[index];
            var last = contour[(index + 1) % contour.Length];
            var steps = Math.Max(1, (int)Math.Ceiling(Distance(first, last)));
            for (var step = 0; step < steps; step++)
            {
                var fraction = (double)step / steps;
                yield return Distance(new(first.X + ((last.X - first.X) * fraction),
                    first.Y + ((last.Y - first.Y) * fraction)), center);
            }
        }
    }

    private static double CrossAngle(PrimitiveContour contour, double approximateAngle)
    {
        var real = 0d;
        var imaginary = 0d;
        var signedArea = 0d;
        ReadOnlySpan<double> positions = [.1127016653792583, .5, .8872983346207417];
        ReadOnlySpan<double> weights = [5d / 18, 4d / 9, 5d / 18];
        for (var index = 0; index < contour.Points.Length; index++)
        {
            var first = contour.Points[index];
            var last = contour.Points[(index + 1) % contour.Points.Length];
            var ax = first.X - contour.Center.X;
            var ay = first.Y - contour.Center.Y;
            var bx = last.X - contour.Center.X;
            var by = last.Y - contour.Center.Y;
            var cross = (ax * by) - (ay * bx);
            signedArea += cross;
            for (var sample = 0; sample < positions.Length; sample++)
            {
                var x = ax + ((bx - ax) * positions[sample]);
                var y = ay + ((by - ay) * positions[sample]);
                var xx = x * x;
                var yy = y * y;
                real += ((xx * xx) - (6 * xx * yy) + (yy * yy)) * cross * weights[sample];
                imaginary += 4 * x * y * (xx - yy) * cross * weights[sample];
            }
        }
        // div(xP,yP)=6P인 4차 동차식: 면적 모멘트를 경계 적분한다.
        // 변 위 4차식은 3점 Gauss 적분으로 정확하다. 공통 1/6은 방향에 영향을 주지 않는다.
        var orientation = Math.Sign(signedArea);
        var angle = Math.Atan2(imaginary * orientation, real * orientation) / 4;
        if (Math.Abs(Math.IEEERemainder(angle - approximateAngle, Math.PI / 2)) > Math.PI / 8)
        {
            angle += Math.PI / 4;
        }
        return Math.IEEERemainder(angle, Math.PI / 2);
    }

    private static double OrthogonalAngle(ImmutableArray<ImagePoint> polygon)
    {
        var sine = 0d;
        var cosine = 0d;
        for (var index = 0; index < polygon.Length; index++)
        {
            var first = polygon[index];
            var second = polygon[(index + 1) % polygon.Length];
            var angle = Math.Atan2(second.Y - first.Y, second.X - first.X);
            var length = Distance(first, second);
            sine += length * Math.Sin(4 * angle);
            cosine += length * Math.Cos(4 * angle);
        }
        // 한 변의 픽셀 계단 오차 대신 모든 변을 90° 주기로 결합한다.
        return Math.Atan2(sine, cosine) / 4;
    }

    private static ImmutableArray<ImagePoint> FitStraightEdges(ImmutableArray<ImagePoint> contour, ImmutableArray<ImagePoint> polygon,
        bool rectangleWithBevels = false)
    {
        var edges = rectangleWithBevels
            ? Enumerable.Range(0, polygon.Length).OrderByDescending(index => Distance(polygon[index], polygon[(index + 1) % polygon.Length]))
                .Take(4).Order().ToArray()
            : Enumerable.Range(0, polygon.Length).ToArray();
        var lines = new (ImagePoint Center, ImagePoint Direction)[edges.Length];
        for (var edge = 0; edge < edges.Length; edge++)
        {
            var first = polygon[edges[edge]];
            var last = polygon[(edges[edge] + 1) % polygon.Length];
            if (!TryFitEdgeLine(contour, first, last, out lines[edge]))
            {
                return polygon;
            }
        }
        return TryIntersectFittedLines(lines, out var fitted) ? fitted : polygon;
    }

    private static bool TryFitEdgeLine(ImmutableArray<ImagePoint> contour, ImagePoint first, ImagePoint last,
        out (ImagePoint Center, ImagePoint Direction) line)
    {
        line = default;
        var dx = last.X - first.X;
        var dy = last.Y - first.Y;
        var lengthSquared = (dx * dx) + (dy * dy);
        var end = contour.IndexOf(last);
        var samples = new List<ImagePoint>();
        // 근사 꼭짓점은 median으로 둥글어진 모서리에 치우칠 수 있다.
        // 원본 변의 가운데 80%를 균등하게 표본화하고 직선을 맞춰 실제 교점을 복원한다.
        for (var index = contour.IndexOf(first); index != end; index = (index + 1) % contour.Length)
        {
            var a = contour[index];
            var b = contour[(index + 1) % contour.Length];
            var steps = Math.Max(1, (int)Math.Ceiling(Distance(a, b)));
            for (var step = 0; step < steps; step++)
            {
                var fraction = (step + .5) / steps;
                var sample = new ImagePoint(a.X + ((b.X - a.X) * fraction), a.Y + ((b.Y - a.Y) * fraction));
                var position = (((sample.X - first.X) * dx) + ((sample.Y - first.Y) * dy)) / lengthSquared;
                if (position is >= .1 and <= .9)
                {
                    samples.Add(sample);
                }
            }
        }
        if (samples.Count < 3)
        {
            return false;
        }
        var center = new ImagePoint(samples.Average(point => point.X), samples.Average(point => point.Y));
        var xx = 0d;
        var xy = 0d;
        var yy = 0d;
        foreach (var sample in samples)
        {
            var x = sample.X - center.X;
            var y = sample.Y - center.Y;
            xx += x * x;
            xy += x * y;
            yy += y * y;
        }
        var angle = .5 * Math.Atan2(2 * xy, xx - yy);
        var direction = new ImagePoint(Math.Cos(angle), Math.Sin(angle));
        if (samples.Any(point => Math.Abs(((point.X - center.X) * direction.Y) - ((point.Y - center.Y) * direction.X)) > 1))
        {
            return false;
        }
        line = (center, direction);
        return true;
    }

    private static bool TryIntersectFittedLines((ImagePoint Center, ImagePoint Direction)[] lines,
        out ImmutableArray<ImagePoint> fitted)
    {
        var builder = ImmutableArray.CreateBuilder<ImagePoint>(lines.Length);
        for (var index = 0; index < lines.Length; index++)
        {
            var previous = lines[(index + lines.Length - 1) % lines.Length];
            var current = lines[index];
            var denominator = (previous.Direction.X * current.Direction.Y) - (previous.Direction.Y * current.Direction.X);
            if (Math.Abs(denominator) < .1)
            {
                fitted = default;
                return false;
            }
            var distance = (((current.Center.X - previous.Center.X) * current.Direction.Y) -
                ((current.Center.Y - previous.Center.Y) * current.Direction.X)) / denominator;
            builder.Add(new(previous.Center.X + (distance * previous.Direction.X), previous.Center.Y + (distance * previous.Direction.Y)));
        }
        fitted = builder.MoveToImmutable();
        return true;
    }

    private static double[] Cluster(IEnumerable<double> input, double tolerance)
    {
        var clusters = new List<List<double>>();
        foreach (var value in input.Order())
        {
            if (clusters.Count == 0 || value - clusters[^1][0] > tolerance)
            {
                clusters.Add([]);
            }
            clusters[^1].Add(value);
        }
        return clusters.Select(values => values.Average()).ToArray();
    }

    private static bool Orthogonal(ImmutableArray<ImagePoint> polygon, double tolerance)
    {
        for (var i = 0; i < polygon.Length; i++)
        {
            var previous = polygon[(i + polygon.Length - 1) % polygon.Length];
            var current = polygon[i];
            var next = polygon[(i + 1) % polygon.Length];
            var cosine = ((previous.X - current.X) * (next.X - current.X)) + ((previous.Y - current.Y) * (next.Y - current.Y));
            if (Math.Abs(cosine / (Distance(previous, current) * Distance(next, current))) > tolerance)
            {
                return false;
            }
        }
        return true;
    }

    private static bool Convex(ImmutableArray<ImagePoint> points)
    {
        var signs = new HashSet<int>();
        for (var i = 0; i < points.Length; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % points.Length];
            var c = points[(i + 2) % points.Length];
            signs.Add(Math.Sign(((b.X - a.X) * (c.Y - b.Y)) - ((b.Y - a.Y) * (c.X - b.X))));
        }
        signs.Remove(0);
        return signs.Count == 1;
    }

    private static double Area(ImmutableArray<ImagePoint> points)
        => Math.Abs(points.Select((point, index) => (point.X * points[(index + 1) % points.Length].Y) -
            (point.Y * points[(index + 1) % points.Length].X)).Sum()) / 2;

    private static double Distance(ImagePoint a, ImagePoint b) => Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));
}
