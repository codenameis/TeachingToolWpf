namespace VisionGeometry;

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
