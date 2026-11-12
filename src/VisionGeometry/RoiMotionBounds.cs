namespace VisionGeometry;

/// <summary>처음 경계를 만날 때까지의 연속 이동/회전 범위. 끝점만 유효한 도약은 허용하지 않는다.</summary>
public static class RoiMotionBounds
{
    /// <summary>접촉 위치에서 벽의 접선으로 남은 이동을 투영한다. 호출자가 전체 선택의 경로를 다시 검증한다.</summary>
    public static void CollectSlidingTranslations(RoiGeometry parent, RoiGeometry child, PointD remaining, List<PointD> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (parent.Radius > 0)
        {
            var radius = parent.Radius - child.Radius;
            if (child.Radius > 0)
            {
                AddCircularSlide(parent.Center, radius, child.Center, remaining, destination);
                return;
            }

            var count = child.VertexCount;
            Span<PointD> childPoints = count <= 64 ? stackalloc PointD[count] : new PointD[count];
            child.CopyOutlineTo(childPoints);
            for (var i = 0; i < childPoints.Length; i++)
            {
                AddCircularSlide(parent.Center, radius, childPoints[i], remaining, destination);
            }

            return;
        }

        var parentCount = parent.VertexCount;
        Span<PointD> outline = parentCount <= 64 ? stackalloc PointD[parentCount] : new PointD[parentCount];
        parent.CopyOutlineTo(outline);

        var childCount = child.Radius > 0 ? 0 : child.VertexCount;
        Span<PointD> childOutline = childCount <= 64 ? stackalloc PointD[childCount] : new PointD[childCount];
        if (childCount > 0)
        {
            child.CopyOutlineTo(childOutline);
        }

        var tolerance = Math.Max(1e-5, Math.Max(Math.Abs(parent.Center.X), Math.Abs(parent.Center.Y)) * 1e-11);
        for (var i = 0; i < outline.Length; i++)
        {
            var a = outline[i];
            var b = outline[(i + 1) % outline.Length];
            var touches = false;
            if (child.Radius > 0)
            {
                touches = PolygonMath.Distance(child.Center, a, b) <= child.Radius + tolerance;
            }
            else
            {
                for (var k = 0; k < childOutline.Length; k++)
                {
                    if (PolygonMath.Distance(childOutline[k], a, b) <= tolerance)
                    {
                        touches = true;
                        break;
                    }
                }
            }

            if (touches)
            {
                destination.Add(ProjectAlong(remaining, b - a));
            }

            // 오목 부모의 꼭짓점이 자식 변에 닿는 경우에는 그 자식 변도 이동 경계다.
            for (var j = 0; j < childOutline.Length; j++)
            {
                var c = childOutline[j];
                var d = childOutline[(j + 1) % childOutline.Length];
                if (PolygonMath.Distance(a, c, d) <= tolerance)
                {
                    destination.Add(ProjectAlong(remaining, d - c));
                }
            }
        }
    }

    public static IEnumerable<PointD> SlidingTranslations(RoiGeometry parent, RoiGeometry child, PointD remaining)
    {
        var results = new List<PointD>(8);
        CollectSlidingTranslations(parent, child, remaining, results);
        return results;
    }

    private static void AddCircularSlide(PointD parentCenter, double radius, PointD point, PointD remaining, List<PointD> destination)
    {
        var target = point + remaining - parentCenter;
        if (target.Length > radius)
        {
            destination.Add(parentCenter + (target * (radius / target.Length)) - point);
        }
    }

    private static PointD ProjectAlong(PointD remaining, PointD tangent)
        => tangent * (Dot(remaining, tangent) / Dot(tangent, tangent));

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

        var parentTolerance = GeometryTolerance.Scaled(parent.Center);
        var needsParentBoundary = parent.Radius == 0 && parent.Shape.Kind != RoiShapeKind.Rectangle;
        var parentCount = needsParentBoundary ? parent.VertexCount : 0;
        Span<PointD> boundary = parentCount <= 64 ? stackalloc PointD[parentCount] : new PointD[parentCount];
        if (needsParentBoundary)
        {
            parent.CopyOutlineTo(boundary);
        }

        var needsChildBoundary = child.Radius == 0 && (parent.Radius > 0 || needsParentBoundary);
        var childCount = needsChildBoundary ? child.VertexCount : 0;
        Span<PointD> childOutline = childCount <= 64 ? stackalloc PointD[childCount] : new PointD[childCount];
        if (needsChildBoundary)
        {
            child.CopyOutlineTo(childOutline);
        }

        if (!CoversTranslatedChild(parent, child, boundary, childOutline, default, parentTolerance))
        {
            return 0;
        }

        if (FitsTranslationStep(parent, child, boundary, childOutline, delta, parentTolerance))
        {
            return 1;
        }

        var low = 0d;
        var high = 1d;
        for (var iteration = 0; iteration < 40; iteration++)
        {
            var middle = (low + high) / 2;
            if (FitsTranslationStep(parent, child, boundary, childOutline, delta * middle, parentTolerance))
            {
                low = middle;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    private static bool FitsTranslationStep(
        RoiGeometry parent,
        RoiGeometry child,
        ReadOnlySpan<PointD> boundary,
        ReadOnlySpan<PointD> childOutline,
        PointD movement,
        double parentTolerance)
    {
        if (!CoversTranslatedChild(parent, child, boundary, childOutline, movement, parentTolerance))
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
            if (!PolygonMath.ContainsSegment(boundary, child.Center, end, 1e-7))
            {
                return false;
            }

            for (var i = 0; i < boundary.Length; i++)
            {
                var next = (i + 1) % boundary.Length;
                if (SegmentDistance(child.Center, end, boundary[i], boundary[next]) + 1e-7 < child.Radius)
                {
                    return false;
                }
            }

            return true;
        }

        // 각 변이 쓸고 지나가는 띠의 양 끝 변과 꼭짓점 경로가 모두 부모 안이어야 한다.
        for (var i = 0; i < childOutline.Length; i++)
        {
            var start = childOutline[i];
            var end = start + movement;
            if (!PolygonMath.Contains(boundary, end, 1e-7) ||
                !PolygonMath.ContainsSegment(boundary, start, end, 1e-7))
            {
                return false;
            }
        }

        return true;
    }

    private static bool CoversTranslatedChild(
        RoiGeometry parent,
        RoiGeometry child,
        ReadOnlySpan<PointD> parentBoundary,
        ReadOnlySpan<PointD> childOutline,
        PointD movement,
        double tolerance)
    {
        if (parent.Radius > 0)
        {
            if (child.Radius > 0)
            {
                var movedCenter = child.Center + movement;
                return (parent.Center - movedCenter).Length + child.Radius <= parent.Radius + tolerance;
            }

            for (var i = 0; i < childOutline.Length; i++)
            {
                if (!parent.Contains(childOutline[i] + movement, tolerance))
                {
                    return false;
                }
            }

            return true;
        }

        if (parent.Shape.Kind == RoiShapeKind.Rectangle)
        {
            return parent.Covers(child.Translate(movement));
        }

        if (child.Radius > 0)
        {
            var movedCenter = child.Center + movement;
            if (!parent.Contains(movedCenter, tolerance))
            {
                return false;
            }

            for (var i = 0; i < parentBoundary.Length; i++)
            {
                var next = (i + 1) % parentBoundary.Length;
                if (PolygonMath.Distance(movedCenter, parentBoundary[i], parentBoundary[next]) + tolerance < child.Radius)
                {
                    return false;
                }
            }

            return true;
        }

        for (var i = 0; i < childOutline.Length; i++)
        {
            if (!parent.Contains(childOutline[i] + movement, tolerance))
            {
                return false;
            }
        }

        for (var i = 0; i < childOutline.Length; i++)
        {
            var next = (i + 1) % childOutline.Length;
            if (!PolygonMath.ContainsSegment(parentBoundary, childOutline[i] + movement, childOutline[next] + movement, tolerance))
            {
                return false;
            }
        }

        return true;
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
        var end = Math.Min(Math.Abs(degrees), GeometryConstants.FullCircleDegrees) * Math.PI / GeometryConstants.HalfCircleDegrees;
        var contacts = CollectRotationContacts(parent, child, pivot, direction, end);
        contacts.Sort();
        for (var i = 1; i < contacts.Count; i++)
        {
            var middle = (contacts[i - 1] + contacts[i]) / 2;
            if (!parent.Covers(child.RotateAround(pivot, direction * middle * GeometryConstants.HalfCircleDegrees / Math.PI)))
            {
                return direction * contacts[i - 1] * GeometryConstants.HalfCircleDegrees / Math.PI;
            }
        }

        return degrees;
    }

    private static List<double> CollectRotationContacts(RoiGeometry parent, RoiGeometry child, PointD pivot, int direction, double end)
    {
        var contacts = new List<double> { 0, end };
        var childPointCount = child.Radius > 0 ? 1 : child.VertexCount;
        Span<PointD> points = childPointCount <= 64 ? stackalloc PointD[childPointCount] : new PointD[childPointCount];
        if (child.Radius > 0)
        {
            points[0] = child.Center;
        }
        else
        {
            child.CopyOutlineTo(points);
        }

        if (parent.Radius > 0)
        {
            foreach (var point in points)
            {
                AddCircleContacts(contacts, point, parent.Center, parent.Radius - child.Radius, pivot, direction, end);
            }

            return contacts;
        }

        var parentCount = parent.VertexCount;
        Span<PointD> boundary = parentCount <= 64 ? stackalloc PointD[parentCount] : new PointD[parentCount];
        parent.CopyOutlineTo(boundary);
        for (var i = 0; i < boundary.Length; i++)
        {
            var a = boundary[i];
            var edge = boundary[(i + 1) % boundary.Length] - a;
            AddParentEdgeContacts(contacts, a, edge, points, child.Radius, pivot, direction, end);
            AddParentVertexContacts(contacts, a, points, child, pivot, direction, end);
        }

        return contacts;
    }

    private static void AddParentEdgeContacts(
        List<double> contacts,
        PointD edgeStart,
        PointD edge,
        ReadOnlySpan<PointD> points,
        double childRadius,
        PointD pivot,
        int direction,
        double end)
    {
        foreach (var point in points)
        {
            var relative = point - pivot;
            var offset = PolygonMath.Cross(edge, pivot - edgeStart);
            AddRotationRoots(contacts, PolygonMath.Cross(edge, relative), Dot(edge, relative), -offset + (childRadius * edge.Length), direction, end);
            if (childRadius > 0)
            {
                AddRotationRoots(contacts, PolygonMath.Cross(edge, relative), Dot(edge, relative), -offset - (childRadius * edge.Length), direction, end);
            }
        }
    }

    private static void AddParentVertexContacts(
        List<double> contacts,
        PointD parentVertex,
        ReadOnlySpan<PointD> points,
        RoiGeometry child,
        PointD pivot,
        int direction,
        double end)
    {
        if (child.Radius > 0)
        {
            AddCircleContacts(contacts, child.Center, parentVertex, child.Radius, pivot, direction, end);
            return;
        }

        // 오목 부모의 꼭짓점이 회전하는 자식 변을 가로지르는 접촉도 포함한다.
        var relative = parentVertex - pivot;
        for (var j = 0; j < points.Length; j++)
        {
            var childEdge = points[(j + 1) % points.Length] - points[j];
            AddRotationRoots(
                contacts,
                PolygonMath.Cross(childEdge, relative),
                -Dot(childEdge, relative),
                -PolygonMath.Cross(childEdge, pivot - points[j]),
                direction,
                end);
        }
    }

    private static void AddCircleContacts(
        List<double> contacts,
        PointD point,
        PointD center,
        double radius,
        PointD pivot,
        int direction,
        double end)
    {
        var relative = point - pivot;
        var offset = pivot - center;
        AddRotationRoots(
            contacts,
            2 * Dot(offset, relative),
            2 * PolygonMath.Cross(relative, offset),
            (radius * radius) - Dot(relative, relative) - Dot(offset, offset),
            direction,
            end);
    }

    private static void AddRotationRoots(List<double> contacts, double cosine, double sine, double limit, int direction, double end)
    {
        var magnitude = double.Hypot(cosine, sine);
        if (magnitude < GeometryTolerance.Squared || Math.Abs(limit) > magnitude)
        {
            return;
        }

        var phase = Math.Atan2(sine, cosine);
        var offset = Math.Acos(Math.Clamp(limit / magnitude, -1, 1));
        AddContactAngle(contacts, phase - offset, direction, end);
        AddContactAngle(contacts, phase + offset, direction, end);
    }

    private static void AddContactAngle(List<double> contacts, double angle, int direction, double end)
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

    private static double Dot(PointD a, PointD b) => (a.X * b.X) + (a.Y * b.Y);

    private static double SegmentDistance(PointD a, PointD b, PointD c, PointD d)
    {
        if (a == b)
        {
            return PolygonMath.Distance(a, c, d);
        }

        if (PolygonMath.Intersects(a, b, c, d))
        {
            return 0;
        }

        var distanceFromEndpointsToCd = Math.Min(PolygonMath.Distance(a, c, d), PolygonMath.Distance(b, c, d));
        var distanceFromEndpointsToAb = Math.Min(PolygonMath.Distance(c, a, b), PolygonMath.Distance(d, a, b));
        return Math.Min(distanceFromEndpointsToCd, distanceFromEndpointsToAb);
    }
}
