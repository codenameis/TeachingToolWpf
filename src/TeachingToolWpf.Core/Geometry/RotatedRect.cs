namespace TeachingToolWpf.Core.Geometry;

public sealed record RotatedRect
{
    public PointD Center { get; }
    public double Width { get; }
    public double Height { get; }
    public double Angle { get; }
    public double Area => Width * Height;

    public RotatedRect(PointD center, double width, double height, double angle)
    {
        if (!center.IsFinite) throw new ArgumentOutOfRangeException(nameof(center));
        if (!double.IsFinite(width) || width < 0.01) throw new ArgumentOutOfRangeException(nameof(width));
        if (!double.IsFinite(height) || height < 0.01) throw new ArgumentOutOfRangeException(nameof(height));
        if (!double.IsFinite(angle) || !double.IsFinite(width * height)) throw new ArgumentOutOfRangeException(nameof(angle));
        Center = center;
        Width = width;
        Height = height;
        Angle = NormalizeAngle(angle);
    }

    public static double NormalizeAngle(double angle) => (angle % 360 + 360) % 360;
    public PointD ToWorld(PointD local) => Center + local.Rotate(Angle);
    public PointD ToLocal(PointD world) => (world - Center).Rotate(-Angle);
    public PointD[] Corners =>
    [
        ToWorld(new(-Width / 2, -Height / 2)), ToWorld(new(Width / 2, -Height / 2)),
        ToWorld(new(Width / 2, Height / 2)), ToWorld(new(-Width / 2, Height / 2))
    ];
    public BoundsD Bounds
    {
        get
        {
            var angle = Angle * Math.PI / 180;
            var c = Math.Abs(Math.Cos(angle)); var s = Math.Abs(Math.Sin(angle));
            var x = (Width * c + Height * s) / 2; var y = (Width * s + Height * c) / 2;
            return new(Center.X - x, Center.Y - y, Center.X + x, Center.Y + y);
        }
    }
    public bool Contains(PointD world, double tolerance = 0)
    {
        var local = ToLocal(world);
        return Math.Abs(local.X) <= Width / 2 + tolerance && Math.Abs(local.Y) <= Height / 2 + tolerance;
    }
    public bool Contains(RotatedRect child)
    {
        var tolerance = Math.Max(1e-6, Math.Max(Math.Abs(Center.X), Math.Abs(Center.Y)) * 1e-12);
        if (Area <= child.Area + tolerance * (Width + Height)) return false;
        return child.Corners.All(p => Contains(p, tolerance));
    }
    public RotatedRect Translate(PointD delta) => new(Center + delta, Width, Height, Angle);
    public RotatedRect RotateAround(PointD pivot, double angle) =>
        new(pivot + (Center - pivot).Rotate(angle), Width, Height, Angle + angle);
}
