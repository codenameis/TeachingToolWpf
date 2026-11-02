namespace VisionGeometry;

public readonly record struct BoundsD(double Left, double Top, double Right, double Bottom)
{
    public double Width => Right - Left;

    public double Height => Bottom - Top;

    public PointD Center => new((Left + Right) / 2, (Top + Bottom) / 2);

    public bool Contains(PointD p) => p.X >= Left && p.X <= Right && p.Y >= Top && p.Y <= Bottom;

    public bool Intersects(BoundsD b) => Right >= b.Left && Left <= b.Right && Bottom >= b.Top && Top <= b.Bottom;

    public static BoundsD FromPoints(ReadOnlySpan<PointD> points)
    {
        if (points.IsEmpty)
        {
            throw new ArgumentException("점이 필요합니다.", nameof(points));
        }

        var minX = points[0].X;
        var minY = points[0].Y;
        var maxX = minX;
        var maxY = minY;

        for (var i = 1; i < points.Length; i++)
        {
            var p = points[i];
            if (p.X < minX)
            {
                minX = p.X;
            }

            if (p.X > maxX)
            {
                maxX = p.X;
            }

            if (p.Y < minY)
            {
                minY = p.Y;
            }

            if (p.Y > maxY)
            {
                maxY = p.Y;
            }
        }

        return new(minX, minY, maxX, maxY);
    }

    public static BoundsD FromPoints(IEnumerable<PointD> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points is PointD[] array)
        {
            return FromPoints(array.AsSpan());
        }

        using var enumerator = points.GetEnumerator();
        if (!enumerator.MoveNext())
        {
            throw new ArgumentException("점이 필요합니다.", nameof(points));
        }

        var first = enumerator.Current;
        var minX = first.X;
        var minY = first.Y;
        var maxX = first.X;
        var maxY = first.Y;

        while (enumerator.MoveNext())
        {
            var p = enumerator.Current;
            if (p.X < minX)
            {
                minX = p.X;
            }

            if (p.X > maxX)
            {
                maxX = p.X;
            }

            if (p.Y < minY)
            {
                minY = p.Y;
            }

            if (p.Y > maxY)
            {
                maxY = p.Y;
            }
        }

        return new(minX, minY, maxX, maxY);
    }
}
