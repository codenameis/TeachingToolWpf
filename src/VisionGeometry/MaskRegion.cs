namespace VisionGeometry;

public enum MaskShape
{
    Rectangle,
    Ellipse
}

/// <summary>ROI 프레임 좌상단 기준 0–1 좌표. 적용 영역은 실제 ROI 도형과의 교집합이다.</summary>
public sealed record MaskRegion
{
    public MaskShape Shape
    {
        get;
    }
    public BoundsD Bounds
    {
        get;
    }

    public MaskRegion(MaskShape shape, BoundsD bounds)
    {
        if (!Enum.IsDefined(shape) || !double.IsFinite(bounds.Left) || !double.IsFinite(bounds.Top) ||
            !double.IsFinite(bounds.Right) || !double.IsFinite(bounds.Bottom) || bounds.Left < 0 || bounds.Top < 0 ||
            bounds.Right > 1 || bounds.Bottom > 1 || bounds.Width <= 0 || bounds.Height <= 0)
        {
            throw new ArgumentException("마스크는 ROI 프레임 안의 유효한 영역이어야 합니다.", nameof(bounds));
        }
        Shape = shape;
        Bounds = bounds;
    }

    public bool ContainsNormalized(PointD point)
    {
        if (!Bounds.Contains(point))
        {
            return false;
        }
        if (Shape == MaskShape.Rectangle)
        {
            return true;
        }
        var x = (point.X - Bounds.Center.X) * 2 / Bounds.Width;
        var y = (point.Y - Bounds.Center.Y) * 2 / Bounds.Height;
        return (x * x) + (y * y) <= 1;
    }

    public bool Contains(RoiGeometry owner, PointD world)
    {
        var local = owner.ToLocal(world);
        return owner.Shape.ContainsLocal(local, owner.Width, owner.Height, 0) &&
            ContainsNormalized(new((local.X / owner.Width) + .5, (local.Y / owner.Height) + .5));
    }
}
