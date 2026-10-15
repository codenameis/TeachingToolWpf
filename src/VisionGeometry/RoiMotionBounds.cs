namespace VisionGeometry;

/// <summary>처음 경계를 만날 때까지의 연속 이동/회전 범위. 끝점만 유효한 도약은 허용하지 않는다.</summary>
public static class RoiMotionBounds
{
    /// <summary>접촉 위치에서 벽의 접선으로 남은 이동을 투영한다. 호출자가 전체 선택의 경로를 다시 검증한다.</summary>
    public static IEnumerable<PointD> SlidingTranslations(RoiGeometry parent, RoiGeometry child, PointD remaining)
    {
        if (parent.Radius > 0)
        {
            var radius = parent.Radius - child.Radius;
            foreach (var point in child.Radius > 0 ? new[] { child.Center } : child.Outline)
            {
                var target = point + remaining - parent.Center;
                if (target.Length > radius)
                {
                    yield return parent.Center + (target * (radius / target.Length)) - point;
                }
            }
            yield break;
        }
        var outline = parent.Outline;
        var childOutline = child.Radius > 0 ? [] : child.Outline;
        var tolerance = Math.Max(1e-5, Math.Max(Math.Abs(parent.Center.X), Math.Abs(parent.Center.Y)) * 1e-11);
        for (var i = 0; i < outline.Length; i++)
        {
            var a = outline[i];
            var b = outline[(i + 1) % outline.Length];
            var touches = child.Radius > 0
                ? PolygonMath.Distance(child.Center, a, b) <= child.Radius + tolerance
                : childOutline.Any(point => PolygonMath.Distance(point, a, b) <= tolerance);
            if (touches)
            {
                yield return Along(b - a);
            }
            // 오목 부모의 꼭짓점이 자식 변에 닿는 경우에는 그 자식 변도 이동 경계다.
            for (var j = 0; j < childOutline.Length; j++)
            {
                var c = childOutline[j];
                var d = childOutline[(j + 1) % childOutline.Length];
                if (PolygonMath.Distance(a, c, d) <= tolerance)
                {
                    yield return Along(d - c);
                }
            }
        }
        PointD Along(PointD tangent) => tangent * (Dot(remaining, tangent) / Dot(tangent, tangent));
    }

    public static double TranslationFraction(RoiGeometry parent, RoiGeometry child, PointD delta)
    {
        if (!delta.IsFinite)
        {
            throw new ArgumentOutOfRangeException(nameof(delta));
        }
        if (delta == default)
        {
            return 1;
        }
        if (!parent.Covers(child))
        {
            return 0;
        }
        var boundary = parent.Shape.Kind == RoiShapeKind.Polygon ? parent.Outline : [];
        var childOutline = boundary.Length > 0 && child.Radius == 0 ? child.Outline : [];
        if (Fits(1))
        {
            return 1;
        }
        var low = 0d;
        var high = 1d;
        for (var iteration = 0; iteration < 40; iteration++)
        {
            var middle = (low + high) / 2;
            if (Fits(middle))
            {
                low = middle;
            }
            else
            {
                high = middle;
            }
        }
        return low;

        bool Fits(double fraction)
        {
            var movement = delta * fraction;
            if (!parent.Covers(child.Translate(movement)))
            {
                return false;
            }
            // 볼록한 부모는 두 끝점의 포함으로 이동 구간 전체의 포함이 보장된다.
            if (parent.Shape.Kind != RoiShapeKind.Polygon)
            {
                return true;
            }
            if (child.Radius > 0)
            {
                var end = child.Center + movement;
                return PolygonMath.ContainsSegment(boundary, child.Center, end, 1e-7) &&
                    Enumerable.Range(0, boundary.Length).All(i =>
                        SegmentDistance(child.Center, end, boundary[i], boundary[(i + 1) % boundary.Length]) + 1e-7 >= child.Radius);
            }
            // 각 변이 쓸고 지나가는 띠의 양 끝 변과 꼭짓점 경로가 모두 부모 안이어야 한다.
            return childOutline.All(point => PolygonMath.Contains(boundary, point + movement, 1e-7) &&
                PolygonMath.ContainsSegment(boundary, point, point + movement, 1e-7));
        }
    }

    public static double RotationLimit(RoiGeometry parent, RoiGeometry child, PointD pivot, double degrees)
    {
        if (!pivot.IsFinite || !double.IsFinite(degrees))
        {
            throw new ArgumentOutOfRangeException(nameof(degrees));
        }
        if (degrees == 0 || !parent.Covers(child))
        {
            return 0;
        }
        var direction = Math.Sign(degrees);
        var end = Math.Min(Math.Abs(degrees), 360) * Math.PI / 180;
        var contacts = new List<double> { 0, end };
        if (parent.Radius > 0)
        {
            foreach (var point in child.Radius > 0 ? new[] { child.Center } : child.Outline)
            {
                CircleContacts(point, parent.Center, parent.Radius - child.Radius);
            }
        }
        else
        {
            var boundary = parent.Outline;
            var points = child.Radius > 0 ? new[] { child.Center } : child.Outline;
            for (var i = 0; i < boundary.Length; i++)
            {
                var a = boundary[i];
                var edge = boundary[(i + 1) % boundary.Length] - a;
                foreach (var point in points)
                {
                    var relative = point - pivot;
                    var offset = PolygonMath.Cross(edge, pivot - a);
                    AddRoots(PolygonMath.Cross(edge, relative), Dot(edge, relative), -offset + (child.Radius * edge.Length));
                    if (child.Radius > 0)
                    {
                        AddRoots(PolygonMath.Cross(edge, relative), Dot(edge, relative), -offset - (child.Radius * edge.Length));
                    }
                }
                if (child.Radius > 0)
                {
                    CircleContacts(child.Center, a, child.Radius);
                }
                else
                {
                    // 오목 부모의 꼭짓점이 회전하는 자식 변을 가로지르는 접촉도 포함한다.
                    var relative = a - pivot;
                    for (var j = 0; j < points.Length; j++)
                    {
                        var childEdge = points[(j + 1) % points.Length] - points[j];
                        AddRoots(PolygonMath.Cross(childEdge, relative), -Dot(childEdge, relative),
                            -PolygonMath.Cross(childEdge, pivot - points[j]));
                    }
                }
            }
        }
        contacts.Sort();
        for (var i = 1; i < contacts.Count; i++)
        {
            var middle = (contacts[i - 1] + contacts[i]) / 2;
            if (!parent.Covers(child.RotateAround(pivot, direction * middle * 180 / Math.PI)))
            {
                return direction * contacts[i - 1] * 180 / Math.PI;
            }
        }
        return degrees;

        void CircleContacts(PointD point, PointD center, double radius)
        {
            var relative = point - pivot;
            var offset = pivot - center;
            AddRoots(2 * Dot(offset, relative), 2 * PolygonMath.Cross(relative, offset),
                (radius * radius) - Dot(relative, relative) - Dot(offset, offset));
        }

        void AddRoots(double cosine, double sine, double limit)
        {
            var magnitude = double.Hypot(cosine, sine);
            if (magnitude < 1e-12 || Math.Abs(limit) > magnitude)
            {
                return;
            }
            var phase = Math.Atan2(sine, cosine);
            var offset = Math.Acos(Math.Clamp(limit / magnitude, -1, 1));
            AddAngle(phase - offset);
            AddAngle(phase + offset);
        }

        void AddAngle(double angle)
        {
            var travel = direction * angle % (2 * Math.PI);
            if (travel < 0)
            {
                travel += 2 * Math.PI;
            }
            if (travel > 0 && travel < end)
            {
                contacts.Add(travel);
            }
        }
    }

    private static double Dot(PointD a, PointD b) => (a.X * b.X) + (a.Y * b.Y);

    private static double SegmentDistance(PointD a, PointD b, PointD c, PointD d)
        => a == b ? PolygonMath.Distance(a, c, d) : PolygonMath.Intersects(a, b, c, d) ? 0 : Math.Min(Math.Min(PolygonMath.Distance(a, c, d), PolygonMath.Distance(b, c, d)),
            Math.Min(PolygonMath.Distance(c, a, b), PolygonMath.Distance(d, a, b)));
}
