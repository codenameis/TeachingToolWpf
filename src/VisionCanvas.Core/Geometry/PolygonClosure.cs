using VisionGeometry;
namespace VisionCanvas.Core.Geometry;

/// <summary>새 선분의 첫 교차점부터 이어지는 닫힌 윤곽을 구한다. 열린 꼬리는 포함하지 않는다.</summary>
internal static class PolygonClosure
{
    private static double Cross(PointD a, PointD b) => (a.X * b.Y) - (a.Y * b.X);

    private const double PointTolerance = 1e-6;
    private const double ParameterTolerance = 1e-12;

    public static PointD[]? Find(IReadOnlyList<PointD> points, PointD end)
    {
        if (points.Count < 3)
        {
            return null;
        }

        var start = points[^1];
        var stroke = end - start;
        if (stroke.Length <= PointTolerance)
        {
            return null;
        }

        var (edgeIndex, intersection) = FindFirstIntersection(points, start, stroke);
        return edgeIndex < 0 ? null : BuildClosedLoop(points, edgeIndex, intersection);
    }

    private static (int EdgeIndex, PointD Intersection) FindFirstIntersection(IReadOnlyList<PointD> points, PointD start, PointD stroke)
    {
        var closest = double.PositiveInfinity;
        var edgeIndex = -1;
        var intersection = default(PointD);
        // 마지막 변은 새 선분의 시작점을 공유하므로 닫힘 후보에서 제외한다.
        for (var i = 0; i < points.Count - 2; i++)
        {
            if (TryIntersectEdge(points[i], points[i + 1], start, stroke, closest, out var t, out var hit))
            {
                closest = t;
                edgeIndex = i;
                intersection = hit;
            }
        }
        return (edgeIndex, intersection);
    }

    private static bool TryIntersectEdge(PointD first, PointD second, PointD start, PointD stroke, double closest,
        out double t, out PointD intersection)
    {
        t = 0;
        intersection = default;
        var edge = second - first;
        var divisor = Cross(stroke, edge);
        if (Math.Abs(divisor) <= ParameterTolerance * stroke.Length * edge.Length)
        {
            return false;
        }

        t = Cross(first - start, edge) / divisor;
        var u = Cross(first - start, stroke) / divisor;
        if (t <= ParameterTolerance || t > 1 + ParameterTolerance ||
            u < -ParameterTolerance || u > 1 + ParameterTolerance || t >= closest)
        {
            return false;
        }

        intersection = SnapToEndpoint(first + (edge * Math.Clamp(u, 0, 1)), first, second);
        return true;
    }

    private static PointD SnapToEndpoint(PointD point, PointD first, PointD second)
    {
        if ((point - first).Length <= PointTolerance)
        {
            return first;
        }
        if ((point - second).Length <= PointTolerance)
        {
            return second;
        }
        return point;
    }

    private static PointD[] BuildClosedLoop(IReadOnlyList<PointD> points, int edgeIndex, PointD intersection)
    {
        var closed = new List<PointD> { intersection };
        for (var i = edgeIndex + 1; i < points.Count; i++)
        {
            if ((points[i] - closed[^1]).Length > PointTolerance)
            {
                closed.Add(points[i]);
            }
        }
        return closed.ToArray();
    }
}
