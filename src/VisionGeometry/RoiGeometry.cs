using System.Collections.Immutable;
namespace VisionGeometry;

public sealed record RoiGeometry
{
    public PointD Center { get; }

    public double Width { get; }

    public double Height { get; }

    public double Angle { get; }

    public RoiShape Shape { get; }

    public double Area => Width * Height * Shape.AreaFactor;

    public double Radius => Shape.Radius(Width);

    public PointD ContainmentAnchor => Radius > 0 ? Center : ToWorld(new(Shape.Vertices[0].X * Width, Shape.Vertices[0].Y * Height));

    public int VertexCount => Shape.Vertices.Length;

    public PointD GetVertex(int index)
    {
        var vertex = Shape.Vertices[index];
        return ToWorld(new(vertex.X * Width, vertex.Y * Height));
    }

    public void CopyOutlineTo(Span<PointD> destination)
    {
        var vertices = Shape.Vertices;
        if (destination.Length < vertices.Length)
        {
            throw new ArgumentException("버퍼 크기가 도형 꼭짓점 수보다 작습니다.", nameof(destination));
        }

        for (var i = 0; i < vertices.Length; i++)
        {
            var vertex = vertices[i];
            destination[i] = ToWorld(new(vertex.X * Width, vertex.Y * Height));
        }
    }

    public PointD[] Outline
    {
        get
        {
            var vertices = Shape.Vertices;
            var result = new PointD[vertices.Length];
            CopyOutlineTo(result);
            return result;
        }
    }

    public string ShapeName => Shape.Kind switch
    {
        RoiShapeKind.Circle => "원",
        RoiShapeKind.Triangle => "삼각형",
        RoiShapeKind.Polygon => "폴리곤",
        _ => "사각형"
    };

    public RoiGeometry(PointD center, double width, double height, double angle, RoiShape? shape = null)
    {
        if (!center.IsFinite)
        {
            throw new ArgumentOutOfRangeException(nameof(center));
        }

        if (!double.IsFinite(width) || width < 0.01)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (!double.IsFinite(height) || height < 0.01)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        if (!double.IsFinite(angle) || !double.IsFinite(width * height))
        {
            throw new ArgumentOutOfRangeException(nameof(angle));
        }

        Center = center;
        Width = width;
        Height = height;
        Angle = NormalizeAngle(angle);
        Shape = shape ?? RoiShape.Rectangle;
        if (!double.IsFinite(Area) || Area <= 0)
        {
            throw new ArgumentException("도형 면적은 유한한 양수여야 합니다.", nameof(shape));
        }

        if (Shape.Kind == RoiShapeKind.Circle && width != height)
        {
            throw new ArgumentException("원의 너비와 높이는 지름으로 같아야 합니다.");
        }
    }

    public RoiGeometry WithFrame(PointD center, double width, double height, double angle)
        => new(center, width, height, angle, Shape);

    /// <summary>기존 각도를 유지하며 모든 도형을 감싸는 사각형으로 확장한다. 이미 포함하면 원래 도형을 반환한다.</summary>
    public RoiGeometry ExpandToCover(IEnumerable<RoiGeometry> members)
    {
        var xAxis = new PointD(1, 0).Rotate(Angle);
        var yAxis = new PointD(0, 1).Rotate(Angle);
        var originX = Dot(Center, xAxis);
        var originY = Dot(Center, yAxis);
        var left = -Width / 2;
        var right = Width / 2;
        var top = -Height / 2;
        var bottom = Height / 2;
        var expanded = false;
        foreach (var member in members)
        {
            expanded |= !Covers(member);
            left = Math.Min(left, -member.Support(xAxis * -1) - originX);
            right = Math.Max(right, member.Support(xAxis) - originX);
            top = Math.Min(top, -member.Support(yAxis * -1) - originY);
            bottom = Math.Max(bottom, member.Support(yAxis) - originY);
        }
        var bounds = new BoundsD(left, top, right, bottom);
        return expanded ? new(ToWorld(bounds.Center), bounds.Width, bounds.Height, Angle) : this;
    }

    public static RoiGeometry Create(RoiShapeKind kind, PointD center, double width, double height)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (kind == RoiShapeKind.Circle)
        {
            return new(center, Math.Min(width, height), Math.Min(width, height), 0, RoiShape.Circle);
        }

        if (kind == RoiShapeKind.Triangle)
        {
            return new(center, width, height, 0, RoiShape.Triangle);
        }

        if (kind == RoiShapeKind.Polygon)
        {
            return Polygon(Enumerable.Range(0, 5).Select(i => center + new PointD(Math.Sin(i * Math.PI * 2 / 5) * width / 2, -Math.Cos(i * Math.PI * 2 / 5) * height / 2)));
        }

        return new(center, width, height, 0);
    }

    public static RoiGeometry Polygon(IEnumerable<PointD> vertices)
    {
        var points = vertices as PointD[] ?? vertices.ToArray();
        PolygonMath.Validate(points);
        var bounds = BoundsD.FromPoints(points.AsSpan());
        var normalized = ImmutableArray.CreateBuilder<PointD>(points.Length);
        for (var i = 0; i < points.Length; i++)
        {
            var p = points[i];
            normalized.Add(new PointD((p.X - bounds.Center.X) / bounds.Width, (p.Y - bounds.Center.Y) / bounds.Height));
        }

        return new(bounds.Center, bounds.Width, bounds.Height, 0, new RoiShape.PolygonShape(RoiShapeKind.Polygon, normalized.MoveToImmutable()));
    }

    public double Support(PointD direction) => (Center.X * direction.X) + (Center.Y * direction.Y)
        + Shape.Support(direction.Rotate(-Angle), Width, Height);

    public static double NormalizeAngle(double angle)
        => ((angle % GeometryConstants.FullCircleDegrees) + GeometryConstants.FullCircleDegrees) % GeometryConstants.FullCircleDegrees;

    public PointD ToWorld(PointD local) => Center + local.Rotate(Angle);

    public PointD ToLocal(PointD world) => (world - Center).Rotate(-Angle);

    public QuadPoints FrameCorners => new(
        ToWorld(new(-Width / 2, -Height / 2)),
        ToWorld(new(Width / 2, -Height / 2)),
        ToWorld(new(Width / 2, Height / 2)),
        ToWorld(new(-Width / 2, Height / 2)));

    public BoundsD Bounds => Shape.ComputeBounds(Center, Width, Height, Angle);

    /// <summary>점이 도형 안인지 확인한다. <paramref name="tolerance"/>만큼은 경계 밖도 허용한다.</summary>
    public bool Contains(PointD world, double tolerance = 0)
    {
        var local = ToLocal(world);
        return Shape.ContainsLocal(local, Width, Height, tolerance);
    }

    /// <summary>자식 도형이 이 도형 안에 완전히 들어가는지 확인한다. 크기가 같거나 경계가 겹치면 false다.</summary>
    /// <remarks>
    /// "이 ROI가 부모가 될 수 있는가"를 판정할 때 쓴다. 경계가 정확히 닿는 경우까지 포함하려면 <see cref="Covers"/>를 쓴다.
    /// 면적이 같으면 계산 없이 false를 돌려주는 것은 최적화이며, 판정 결과는 <see cref="Covers"/>와 같다.
    /// </remarks>
    public bool Contains(RoiGeometry child)
    {
        var tolerance = GeometryTolerance.Scaled(Center);
        if (Area <= child.Area + (tolerance * (Width + Height)))
        {
            return false;
        }

        return Covers(child);
    }

    /// <summary>경계가 닿는 경우까지 포함해 판정한다. <see cref="Contains(RoiGeometry)"/>보다 한 단계 느슨하다.</summary>
    public bool Covers(RoiGeometry child)
    {
        var tolerance = GeometryTolerance.Scaled(Center);
        if (Radius > 0)
        {
            if (child.Radius > 0)
            {
                return (Center - child.Center).Length + child.Radius <= Radius + tolerance;
            }

            var childVertexCount = child.VertexCount;
            for (var i = 0; i < childVertexCount; i++)
            {
                if (!Contains(child.GetVertex(i), tolerance))
                {
                    return false;
                }
            }

            return true;
        }

        if (Shape.Kind == RoiShapeKind.Rectangle)
        {
            var x = new PointD(1, 0).Rotate(Angle);
            var y = new PointD(0, 1).Rotate(Angle);
            return child.Support(x) - Dot(Center, x) <= (Width / 2) + tolerance &&
                child.Support(x * -1) + Dot(Center, x) <= (Width / 2) + tolerance &&
                child.Support(y) - Dot(Center, y) <= (Height / 2) + tolerance &&
                child.Support(y * -1) + Dot(Center, y) <= (Height / 2) + tolerance;
        }

        var parentCount = VertexCount;
        Span<PointD> boundary = parentCount <= 64 ? stackalloc PointD[parentCount] : new PointD[parentCount];
        CopyOutlineTo(boundary);
        return CoversWithBoundary(boundary, child, tolerance);
    }

    internal bool CoversWithBoundary(ReadOnlySpan<PointD> boundary, RoiGeometry child, double tolerance)
    {
        if (child.Radius > 0)
        {
            if (!Contains(child.Center, tolerance))
            {
                return false;
            }

            for (var i = 0; i < boundary.Length; i++)
            {
                var next = (i + 1) % boundary.Length;
                if (PolygonMath.Distance(child.Center, boundary[i], boundary[next]) + tolerance < child.Radius)
                {
                    return false;
                }
            }

            return true;
        }

        var childCount = child.VertexCount;
        Span<PointD> childBoundary = childCount <= 64 ? stackalloc PointD[childCount] : new PointD[childCount];
        child.CopyOutlineTo(childBoundary);
        for (var i = 0; i < childBoundary.Length; i++)
        {
            if (!Contains(childBoundary[i], tolerance))
            {
                return false;
            }
        }

        for (var i = 0; i < childBoundary.Length; i++)
        {
            var next = (i + 1) % childBoundary.Length;
            if (!PolygonMath.ContainsSegment(boundary, childBoundary[i], childBoundary[next], tolerance))
            {
                return false;
            }
        }

        return true;
    }

    private static double Dot(PointD a, PointD b) => (a.X * b.X) + (a.Y * b.Y);

    public RoiGeometry Translate(PointD delta) => WithFrame(Center + delta, Width, Height, Angle);

    public RoiGeometry RotateAround(PointD pivot, double angle) =>
        WithFrame(pivot + (Center - pivot).Rotate(angle), Width, Height, Angle + angle);
}
