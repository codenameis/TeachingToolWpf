using System.Collections.Immutable;
namespace TeachingToolWpf.Core.Geometry;

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
    public PointD[] Outline => Shape.Vertices.Select(p => ToWorld(new(p.X * Width, p.Y * Height))).ToArray();
    public string ShapeName => Shape.Kind switch { RoiShapeKind.Circle => "원", RoiShapeKind.Triangle => "삼각형", RoiShapeKind.Polygon => "폴리곤", _ => "사각형" };

    public RoiGeometry(PointD center, double width, double height, double angle, RoiShape? shape = null)
    {
        if (!center.IsFinite) throw new ArgumentOutOfRangeException(nameof(center));
        if (!double.IsFinite(width) || width < 0.01) throw new ArgumentOutOfRangeException(nameof(width));
        if (!double.IsFinite(height) || height < 0.01) throw new ArgumentOutOfRangeException(nameof(height));
        if (!double.IsFinite(angle) || !double.IsFinite(width * height)) throw new ArgumentOutOfRangeException(nameof(angle));
        Center = center;
        Width = width;
        Height = height;
        Angle = NormalizeAngle(angle);
        Shape = shape ?? RoiShape.Rectangle;
        if (!double.IsFinite(Area) || Area <= 0) throw new ArgumentException("도형 면적은 유한한 양수여야 합니다.", nameof(shape));
        if (Shape.Kind == RoiShapeKind.Circle && width != height) throw new ArgumentException("원의 너비와 높이는 지름으로 같아야 합니다.");
    }

    public RoiGeometry WithFrame(PointD center, double width, double height, double angle)
        => new(center, width, height, angle, Shape);
    public static RoiGeometry Create(RoiShapeKind kind, PointD center, double width, double height)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (kind == RoiShapeKind.Circle) return new(center, Math.Min(width, height), Math.Min(width, height), 0, RoiShape.Circle);
        if (kind == RoiShapeKind.Triangle) return new(center, width, height, 0, RoiShape.Triangle);
        if (kind == RoiShapeKind.Polygon)
            return Polygon(Enumerable.Range(0, 5).Select(i => center + new PointD(Math.Sin(i * Math.PI * 2 / 5) * width / 2, -Math.Cos(i * Math.PI * 2 / 5) * height / 2)));
        return new(center, width, height, 0);
    }
    public static RoiGeometry Polygon(IEnumerable<PointD> vertices)
    {
        var points = vertices.ToArray();
        PolygonMath.Validate(points);
        var bounds = BoundsD.FromPoints(points);
        var normalized = points.Select(p => new PointD((p.X - bounds.Center.X) / bounds.Width, (p.Y - bounds.Center.Y) / bounds.Height)).ToImmutableArray();
        return new(bounds.Center, bounds.Width, bounds.Height, 0, new RoiShape.PolygonShape(RoiShapeKind.Polygon, normalized));
    }
    public double Support(PointD direction) => Center.X * direction.X + Center.Y * direction.Y
        + Shape.Support(direction.Rotate(-Angle), Width, Height);

    public static double NormalizeAngle(double angle) => (angle % 360 + 360) % 360;
    public PointD ToWorld(PointD local) => Center + local.Rotate(Angle);
    public PointD ToLocal(PointD world) => (world - Center).Rotate(-Angle);
    public PointD[] FrameCorners =>
    [
        ToWorld(new(-Width / 2, -Height / 2)), ToWorld(new(Width / 2, -Height / 2)),
        ToWorld(new(Width / 2, Height / 2)), ToWorld(new(-Width / 2, Height / 2))
    ];
    public BoundsD Bounds
    {
        get
        {
            if (Radius > 0) return new(Center.X - Radius, Center.Y - Radius, Center.X + Radius, Center.Y + Radius);
            if (Shape.Kind == RoiShapeKind.Rectangle)
            {
                var (sin, cos) = Math.SinCos(Angle * Math.PI / 180);
                var x = (Width * Math.Abs(cos) + Height * Math.Abs(sin)) / 2;
                var y = (Width * Math.Abs(sin) + Height * Math.Abs(cos)) / 2;
                return new(Center.X - x, Center.Y - y, Center.X + x, Center.Y + y);
            }
            return new(-Support(new(-1, 0)), -Support(new(0, -1)), Support(new(1, 0)), Support(new(0, 1)));
        }
    }
    public bool Contains(PointD world, double tolerance = 0)
    {
        var local = ToLocal(world);
        return Shape.ContainsLocal(local, Width, Height, tolerance);
    }
    public bool Contains(RoiGeometry child)
    {
        var tolerance = Math.Max(1e-6, Math.Max(Math.Abs(Center.X), Math.Abs(Center.Y)) * 1e-12);
        if (Area <= child.Area + tolerance * (Width + Height)) return false;
        if (Radius > 0)
            return child.Radius > 0 ? (Center - child.Center).Length + child.Radius <= Radius + tolerance
                : child.Outline.All(p => Contains(p, tolerance));
        if (Shape.Kind == RoiShapeKind.Rectangle)
        {
            var x = new PointD(1, 0).Rotate(Angle); var y = new PointD(0, 1).Rotate(Angle);
            return child.Support(x) - Dot(Center, x) <= Width / 2 + tolerance &&
                child.Support(x * -1) + Dot(Center, x) <= Width / 2 + tolerance &&
                child.Support(y) - Dot(Center, y) <= Height / 2 + tolerance &&
                child.Support(y * -1) + Dot(Center, y) <= Height / 2 + tolerance;
        }
        var boundary = Outline;
        if (child.Radius > 0)
            return Contains(child.Center, tolerance) && Enumerable.Range(0, boundary.Length)
                .All(i => PolygonMath.Distance(child.Center, boundary[i], boundary[(i + 1) % boundary.Length]) + tolerance >= child.Radius);
        var childBoundary = child.Outline;
        return childBoundary.All(p => Contains(p, tolerance)) && Enumerable.Range(0, childBoundary.Length)
            .All(i => PolygonMath.ContainsSegment(boundary, childBoundary[i], childBoundary[(i + 1) % childBoundary.Length], tolerance));
    }
    private static double Dot(PointD a, PointD b) => a.X * b.X + a.Y * b.Y;
    public RoiGeometry Translate(PointD delta) => WithFrame(Center + delta, Width, Height, Angle);
    public RoiGeometry RotateAround(PointD pivot, double angle) =>
        WithFrame(pivot + (Center - pivot).Rotate(angle), Width, Height, Angle + angle);
}
