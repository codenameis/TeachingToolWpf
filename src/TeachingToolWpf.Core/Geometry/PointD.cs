namespace TeachingToolWpf.Core.Geometry;

public readonly record struct PointD(double X, double Y)
{
    public double Length => Math.Sqrt((X * X) + (Y * Y));

    public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y);

    public static PointD operator +(PointD a, PointD b) => new(a.X + b.X, a.Y + b.Y);

    public static PointD operator -(PointD a, PointD b) => new(a.X - b.X, a.Y - b.Y);

    public static PointD operator *(PointD a, double b) => new(a.X * b, a.Y * b);

    public static PointD operator /(PointD a, double b) => new(a.X / b, a.Y / b);

    public PointD Rotate(double degrees)
    {
        var radians = degrees * Math.PI / 180;
        var (sin, cos) = Math.SinCos(radians);
        return new((X * cos) - (Y * sin), (X * sin) + (Y * cos));
    }
}

public readonly record struct BoundsD(double Left, double Top, double Right, double Bottom)
{
    public double Width => Right - Left;

    public double Height => Bottom - Top;

    public PointD Center => new((Left + Right) / 2, (Top + Bottom) / 2);

    public bool Contains(PointD p) => p.X >= Left && p.X <= Right && p.Y >= Top && p.Y <= Bottom;

    public bool Intersects(BoundsD b) => Right >= b.Left && Left <= b.Right && Bottom >= b.Top && Top <= b.Bottom;

    public static BoundsD FromPoints(IEnumerable<PointD> points)
    {
        var array = points.ToArray();
        if (array.Length == 0)
        {
            throw new ArgumentException("점이 필요합니다.", nameof(points));
        }

        return new(array.Min(p => p.X), array.Min(p => p.Y), array.Max(p => p.X), array.Max(p => p.Y));
    }
}
