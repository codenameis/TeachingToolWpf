using System.Collections.Immutable;
using VisionAlgorithm.OpenSrc.PatternMatching;
using VisionAlgorithm.OpenSrc.PatternMatching.Imaging;

namespace VisionAlgorithm.OpenSrc.ShapeDetection;

internal static class PrimitiveContourClassifier
{
    public static PrimitiveShapeDetection? Classify(PrimitiveContour contour, PrimitiveDetectionOptions options)
    {
        var polygon = contour.Approximation;
        if (polygon.Length < 3)
        {
            return null;
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
            return Shape(PrimitiveShapeKind.Rectangle, areaFit);
        }
        if (polygon.Length == 12 && !convex && Orthogonal(polygon, .25) && areaFit >= .88)
        {
            angle = OrthogonalAngle(polygon);
            if (IsCross(polygon, angle, options.ApproximationTolerance))
            {
                return Shape(PrimitiveShapeKind.Cross, areaFit);
            }
        }
        if (polygon.Length >= 8 && convex)
        {
            var radii = contour.Points.Select(point => Distance(point, contour.Center)).ToArray();
            var radius = radii.Average();
            var radialError = Math.Sqrt(radii.Average(value => (value - radius) * (value - radius))) / radius;
            var circularity = 4 * Math.PI * contour.Area / (contour.Perimeter * contour.Perimeter);
            if (radialError <= .05 && radii.Min() / radii.Max() >= .85 && circularity >= .8)
            {
                return Shape(PrimitiveShapeKind.Circle, Math.Clamp(1 - (2 * radialError), 0, 1)) with
                {
                    AngleRadians = 0
                };
            }
        }
        return null;

        PrimitiveShapeDetection Shape(PrimitiveShapeKind kind, double score)
            => new(kind, contour.Center, angle, Math.Clamp(score, 0, 1), contour.Area, contour.Points);
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
