using System.Collections.Immutable;

namespace VisionGeometry;

/// <summary>정규화된 불변 윤곽. 위치·크기·각도는 RoiGeometry가 소유한다.</summary>
public abstract record RoiShape
{
    public static RoiShape Rectangle { get; } = new RectangleShape();

    public static RoiShape Circle { get; } = new CircleShape();

    public static RoiShape Triangle { get; } = new PolygonShape(RoiShapeKind.Triangle,
        [new(0, -.5), new(.5, .5), new(-.5, .5)]);

    public abstract RoiShapeKind Kind { get; }

    public abstract double AreaFactor { get; }

    public abstract ImmutableArray<PointD> Vertices { get; }

    public virtual double Radius(double width) => 0;

    public virtual BoundsD ComputeBounds(PointD center, double width, double height, double angle)
    {
        double SupportWorld(PointD direction)
            => (center.X * direction.X) + (center.Y * direction.Y) + Support(direction.Rotate(-angle), width, height);

        return new(-SupportWorld(new(-1, 0)), -SupportWorld(new(0, -1)), SupportWorld(new(1, 0)), SupportWorld(new(0, 1)));
    }

    public virtual bool ContainsLocal(PointD point, double width, double height, double tolerance)
    {
        var inside = false;
        for (var i = 0; i < Vertices.Length; i++)
        {
            var first = Vertices[i];
            var second = Vertices[(i + 1) % Vertices.Length];
            var a = new PointD(first.X * width, first.Y * height);
            var b = new PointD(second.X * width, second.Y * height);
            if (PolygonMath.Distance(point, a, b) <= Math.Max(1e-10, tolerance))
            {
                return true;
            }

            if ((a.Y > point.Y) != (b.Y > point.Y) &&
                point.X < a.X + ((point.Y - a.Y) * (b.X - a.X) / (b.Y - a.Y)))
            {
                inside = !inside;
            }
        }
        return inside;
    }

    public virtual double Support(PointD direction, double width, double height)
    {
        var maximum = double.NegativeInfinity;
        foreach (var p in Vertices)
        {
            maximum = Math.Max(maximum, (p.X * width * direction.X) + (p.Y * height * direction.Y));
        }

        return maximum;
    }

    private sealed record RectangleShape : RoiShape
    {
        private static readonly ImmutableArray<PointD> s_outline =
        [
            new(-.5, -.5),
            new(.5, -.5),
            new(.5, .5),
            new(-.5, .5)
        ];

        public override RoiShapeKind Kind => RoiShapeKind.Rectangle;

        public override double AreaFactor => 1;

        public override ImmutableArray<PointD> Vertices => s_outline;

        public override BoundsD ComputeBounds(PointD center, double width, double height, double angle)
        {
            // 회전 사각형을 감싸는 축 정렬 경계. 실제 ROI 내부 판정은 Contains가 담당한다.
            var (sine, cosine) = Math.SinCos(angle * Math.PI / GeometryConstants.HalfCircleDegrees);
            var halfBoundsWidth = ((width * Math.Abs(cosine)) + (height * Math.Abs(sine))) / 2;
            var halfBoundsHeight = ((width * Math.Abs(sine)) + (height * Math.Abs(cosine))) / 2;
            return new(center.X - halfBoundsWidth, center.Y - halfBoundsHeight,
                center.X + halfBoundsWidth, center.Y + halfBoundsHeight);
        }

        public override bool ContainsLocal(PointD point, double width, double height, double tolerance)
            => Math.Abs(point.X) <= (width / 2) + tolerance && Math.Abs(point.Y) <= (height / 2) + tolerance;

        public override double Support(PointD direction, double width, double height)
            => (Math.Abs(direction.X) * width / 2) + (Math.Abs(direction.Y) * height / 2);
    }

    private sealed record CircleShape : RoiShape
    {
        private const int OutlineVertexCount = 128;

        private static readonly ImmutableArray<PointD> s_outline = Enumerable.Range(0, OutlineVertexCount)
            .Select(i => new PointD(.5, 0).Rotate(i * GeometryConstants.FullCircleDegrees / OutlineVertexCount)).ToImmutableArray();

        public override RoiShapeKind Kind => RoiShapeKind.Circle;

        public override double AreaFactor => Math.PI / 4;

        public override ImmutableArray<PointD> Vertices => s_outline;

        public override double Radius(double width) => width / 2;

        public override BoundsD ComputeBounds(PointD center, double width, double height, double angle)
        {
            var radius = Radius(width);
            return new(center.X - radius, center.Y - radius, center.X + radius, center.Y + radius);
        }

        public override bool ContainsLocal(PointD point, double width, double height, double tolerance)
            => point.Length <= (width / 2) + tolerance;

        public override double Support(PointD direction, double width, double height) => direction.Length * width / 2;
    }

    internal sealed record PolygonShape : RoiShape
    {
        public override RoiShapeKind Kind
        {
            get;
        }

        public override double AreaFactor
        {
            get;
        }

        public override ImmutableArray<PointD> Vertices
        {
            get;
        }

        public PolygonShape(RoiShapeKind kind, ImmutableArray<PointD> vertices)
        {
            Kind = kind;
            Vertices = vertices;
            AreaFactor = Math.Abs(PolygonMath.SignedArea(vertices));
        }
    }
}

internal static class PolygonMath
{
    public static double Cross(PointD a, PointD b) => (a.X * b.Y) - (a.Y * b.X);

    public static double SignedArea(IReadOnlyList<PointD> points)
    {
        var sum = 0d;
        // 큰 이미지 좌표에서도 원점 오프셋에 의한 상쇄를 줄인다.
        for (var i = 1; i + 1 < points.Count; i++)
        {
            sum += Cross(points[i] - points[0], points[i + 1] - points[0]);
        }

        return sum / 2;
    }

    public static double Distance(PointD point, PointD a, PointD b)
    {
        var edge = b - a;
        var t = Math.Clamp((((point.X - a.X) * edge.X) + ((point.Y - a.Y) * edge.Y)) /
            ((edge.X * edge.X) + (edge.Y * edge.Y)), 0, 1);
        return (point - (a + (edge * t))).Length;
    }

    public static bool Contains(IReadOnlyList<PointD> vertices, PointD point, double tolerance)
    {
        var inside = false;
        for (var i = 0; i < vertices.Count; i++)
        {
            var a = vertices[i];
            var b = vertices[(i + 1) % vertices.Count];
            if (Distance(point, a, b) <= Math.Max(1e-10, tolerance))
            {
                return true;
            }

            if ((a.Y > point.Y) != (b.Y > point.Y) &&
                point.X < a.X + ((point.Y - a.Y) * (b.X - a.X) / (b.Y - a.Y)))
            {
                inside = !inside;
            }
        }
        return inside;
    }

    public static bool Contains(ReadOnlySpan<PointD> vertices, PointD point, double tolerance)
    {
        var inside = false;
        for (var i = 0; i < vertices.Length; i++)
        {
            var a = vertices[i];
            var b = vertices[(i + 1) % vertices.Length];
            if (Distance(point, a, b) <= Math.Max(1e-10, tolerance))
            {
                return true;
            }

            if ((a.Y > point.Y) != (b.Y > point.Y) &&
                point.X < a.X + ((point.Y - a.Y) * (b.X - a.X) / (b.Y - a.Y)))
            {
                inside = !inside;
            }
        }
        return inside;
    }

    public static bool Intersects(PointD a, PointD b, PointD c, PointD d)
    {
        var ab = b - a;
        var cd = d - c;
        var divisor = Cross(ab, cd);
        if (Math.Abs(divisor) < GeometryTolerance.Squared)
        {
            return Distance(a, c, d) < GeometryTolerance.CollinearDistance
                || Distance(b, c, d) < GeometryTolerance.CollinearDistance
                || Distance(c, a, b) < GeometryTolerance.CollinearDistance
                || Distance(d, a, b) < GeometryTolerance.CollinearDistance;
        }

        var t = Cross(c - a, cd) / divisor;
        var u = Cross(c - a, ab) / divisor;
        return t >= 0 && t <= 1 && u >= 0 && u <= 1;
    }

    public static void Validate(IReadOnlyList<PointD> points)
    {
        if (points.Count < 3 || points.Any(p => !p.IsFinite))
        {
            throw new ArgumentException("폴리곤은 유한한 꼭짓점 3개 이상이 필요합니다.");
        }

        if (Math.Abs(SignedArea(points)) < GeometryTolerance.MinimumSignedArea)
        {
            throw new ArgumentException("폴리곤 면적은 0보다 커야 합니다.");
        }

        for (var i = 0; i < points.Count; i++)
        {
            var next = (i + 1) % points.Count;
            if ((points[i] - points[next]).Length < GeometryTolerance.Base)
            {
                throw new ArgumentException("중복 꼭짓점은 사용할 수 없습니다.");
            }

            var incoming = points[i] - points[(i + points.Count - 1) % points.Count];
            var outgoing = points[next] - points[i];
            if (Math.Abs(Cross(incoming, outgoing)) < GeometryTolerance.Squared && (incoming.X * outgoing.X) + (incoming.Y * outgoing.Y) <= 0)
            {
                throw new ArgumentException("폴리곤의 인접한 변이 되접힐 수 없습니다.");
            }

            for (var j = i + 1; j < points.Count; j++)
            {
                var end = (j + 1) % points.Count;
                if (j == next || end == i)
                {
                    continue;
                }

                if (Intersects(points[i], points[next], points[j], points[end]))
                {
                    throw new ArgumentException("폴리곤의 변은 서로 교차하거나 닿을 수 없습니다.");
                }
            }
        }
    }

    // 오목 부모의 홈을 가로지르는 변도 거부한다. 모든 교점 사이의 열린 구간을 판정한다.
    public static bool ContainsSegment(IReadOnlyList<PointD> parent, PointD a, PointD b, double tolerance)
    {
        Span<PointD> vertices = parent.Count <= 64 ? stackalloc PointD[parent.Count] : new PointD[parent.Count];
        for (var i = 0; i < parent.Count; i++)
        {
            vertices[i] = parent[i];
        }

        return ContainsSegment(vertices, a, b, tolerance);
    }

    public static bool ContainsSegment(ReadOnlySpan<PointD> parent, PointD a, PointD b, double tolerance)
    {
        var maxCuts = parent.Length + 2;
        Span<double> cuts = maxCuts <= 66 ? stackalloc double[maxCuts] : new double[maxCuts];
        cuts[0] = 0;
        cuts[1] = 1;
        var cutCount = 2;
        var edge = b - a;
        for (var i = 0; i < parent.Length; i++)
        {
            var c = parent[i];
            var d = parent[(i + 1) % parent.Length];
            var divisor = Cross(edge, d - c);
            if (Math.Abs(divisor) < GeometryTolerance.Squared)
            {
                continue;
            }

            var t = Cross(c - a, d - c) / divisor;
            var u = Cross(c - a, edge) / divisor;
            if (t > 0 && t < 1 && u >= 0 && u <= 1)
            {
                cuts[cutCount++] = t;
            }
        }

        var activeCuts = cuts[..cutCount];
        activeCuts.Sort();
        for (var i = 1; i < activeCuts.Length; i++)
        {
            var midpoint = a + (edge * ((activeCuts[i - 1] + activeCuts[i]) / 2));
            if (!Contains(parent, midpoint, tolerance))
            {
                return false;
            }
        }

        return true;
    }
}
