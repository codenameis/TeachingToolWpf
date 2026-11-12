using System.Collections.Immutable;

namespace VisionAlgorithm.OpenSrc.PatternMatching;

/// <summary>검색 영역은 모델 기준점의 허용 위치다. 영상 밖 특징은 점수 0이며 분모에는 남는다.</summary>
public sealed class ShapeSearchRegion
{
    private readonly ShapeSearchRegion[]? _intersection;
    private readonly ShapeSearchRegion? _expandedRegion;
    private readonly double _margin;

    private ShapeSearchRegion(ShapeSearchRegion region, double margin)
    {
        _expandedRegion = region;
        _margin = margin;
        Boundary = [];
    }

    public ShapeSearchRegion ExpandedBy(double margin)
    {
        if (!double.IsFinite(margin) || margin < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(margin));
        }
        return new(this, margin);
    }

    private ShapeSearchRegion(ShapeSearchRegion first, ShapeSearchRegion second)
    {
        _intersection = [first, second];
        Boundary = [];
    }

    public static ShapeSearchRegion Intersect(ShapeSearchRegion first, ShapeSearchRegion? second)
        => second is null ? first : new(first, second);

    public ImmutableArray<ImagePoint> Boundary { get; }
    public ImagePoint? CircleCenter { get; }
    public double Radius { get; }

    public ShapeSearchRegion(ImagePoint center, double radius)
    {
        if (!double.IsFinite(center.X) || !double.IsFinite(center.Y) || !double.IsFinite(radius) || radius <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(radius));
        }
        CircleCenter = center;
        Radius = radius;
        Boundary = [];
    }

    public ShapeSearchRegion(IEnumerable<ImagePoint> boundary)
    {
        Boundary = boundary.ToImmutableArray();
        if (Boundary.Length < 3 || Boundary.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y)))
        {
            throw new ArgumentException("검색 영역에는 유한한 꼭짓점이 세 개 이상 필요합니다.", nameof(boundary));
        }
    }

    public bool Contains(double x, double y)
    {
        if (_expandedRegion is { } expanded)
        {
            return expanded.ContainsWithin(x, y, _margin);
        }
        if (_intersection is { } regions)
        {
            return regions.All(region => region.Contains(x, y));
        }
        if (CircleCenter is { } center)
        {
            return ((x - center.X) * (x - center.X)) + ((y - center.Y) * (y - center.Y)) <= Radius * Radius;
        }
        var inside = false;
        for (var i = 0; i < Boundary.Length; i++)
        {
            var a = Boundary[i];
            var b = Boundary[(i + 1) % Boundary.Length];
            var cross = ((x - a.X) * (b.Y - a.Y)) - ((y - a.Y) * (b.X - a.X));
            if (Math.Abs(cross) < 1e-8 && x >= Math.Min(a.X, b.X) && x <= Math.Max(a.X, b.X) &&
                y >= Math.Min(a.Y, b.Y) && y <= Math.Max(a.Y, b.Y))
            {
                return true;
            }
            if ((a.Y > y) != (b.Y > y) && x < a.X + ((y - a.Y) * (b.X - a.X) / (b.Y - a.Y)))
            {
                inside = !inside;
            }
        }
        return inside;
    }

    internal bool ContainsWithin(double x, double y, double margin)
    {
        if (_expandedRegion is { } expanded)
        {
            return expanded.ContainsWithin(x, y, margin + _margin);
        }
        if (_intersection is { } regions)
        {
            return regions.All(region => region.ContainsWithin(x, y, margin));
        }
        if (Contains(x, y))
        {
            return true;
        }
        if (CircleCenter is { } center)
        {
            return Math.Sqrt(((x - center.X) * (x - center.X)) + ((y - center.Y) * (y - center.Y))) <= Radius + margin;
        }
        for (var index = 0; index < Boundary.Length; index++)
        {
            var a = Boundary[index];
            var b = Boundary[(index + 1) % Boundary.Length];
            var dx = b.X - a.X;
            var dy = b.Y - a.Y;
            var lengthSquared = (dx * dx) + (dy * dy);
            var fraction = lengthSquared == 0 ? 0 : Math.Clamp((((x - a.X) * dx) + ((y - a.Y) * dy)) / lengthSquared, 0, 1);
            var ex = x - a.X - (fraction * dx);
            var ey = y - a.Y - (fraction * dy);
            if ((ex * ex) + (ey * ey) <= margin * margin)
            {
                return true;
            }
        }
        return false;
    }
}
