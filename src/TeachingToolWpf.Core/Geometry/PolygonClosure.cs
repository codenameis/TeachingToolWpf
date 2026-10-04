namespace TeachingToolWpf.Core.Geometry;

/// <summary>새 선분의 첫 교차점부터 이어지는 닫힌 윤곽을 구한다. 열린 꼬리는 포함하지 않는다.</summary>
internal static class PolygonClosure
{
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

        var closest = double.PositiveInfinity;
        var edgeIndex = -1;
        var intersection = default(PointD);
        // 마지막 변은 새 선분의 시작점을 공유하므로 닫힘 후보에서 제외한다.
        for (var i = 0; i < points.Count - 2; i++)
        {
            var first = points[i];
            var second = points[i + 1];
            var edge = second - first;
            var divisor = PolygonMath.Cross(stroke, edge);
            if (Math.Abs(divisor) <= ParameterTolerance * stroke.Length * edge.Length)
            {
                continue;
            }

            var t = PolygonMath.Cross(first - start, edge) / divisor;
            var u = PolygonMath.Cross(first - start, stroke) / divisor;
            if (t <= ParameterTolerance || t > 1 + ParameterTolerance ||
                u < -ParameterTolerance || u > 1 + ParameterTolerance || t >= closest)
            {
                continue;
            }

            closest = t;
            edgeIndex = i;
            intersection = first + (edge * Math.Clamp(u, 0, 1));
            if ((intersection - first).Length <= PointTolerance)
            {
                intersection = first;
            }
            else if ((intersection - second).Length <= PointTolerance)
            {
                intersection = second;
            }
        }

        if (edgeIndex < 0)
        {
            return null;
        }

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
