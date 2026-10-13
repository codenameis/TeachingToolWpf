namespace VisionGeometry;

/// <summary>기하·입력·렌더가 공유하는 이미지 좌표의 강체 변환(회전과 이동). 기본값은 항등 변환이다.</summary>
public readonly record struct RigidTransform2D
{
    public PointD Pivot
    {
        get;
    }

    public double AngleDegrees
    {
        get;
    }

    public PointD Translation
    {
        get;
    }

    private readonly double _cosine;

    private readonly double _sine;

    public double Cosine => AngleDegrees == 0 ? 1 : _cosine;

    public double Sine => _sine;

    public bool IsIdentity => AngleDegrees % 360 == 0 && Translation == default;

    public static RigidTransform2D Identity => default;

    public RigidTransform2D(PointD pivot, double angleDegrees, PointD translation = default)
    {
        if (!pivot.IsFinite || !translation.IsFinite || !double.IsFinite(angleDegrees))
        {
            throw new ArgumentOutOfRangeException(nameof(angleDegrees), "변환에는 유한한 좌표와 각도가 필요합니다.");
        }

        Pivot = pivot;
        Translation = translation;
        AngleDegrees = angleDegrees;
        var reduced = Math.IEEERemainder(angleDegrees, 360);
        (_sine, _cosine) = reduced == 0 ? (0, 1) : Math.SinCos(reduced * Math.PI / 180);
    }

    // 회전 중심을 원점으로 옮겨 회전한 다음 중심과 이동량을 더한다. p' = c + R(p - c) + t.
    public PointD Apply(PointD point) => AngleDegrees % 360 == 0
        ? point + Translation
        : Pivot + ApplyVector(point - Pivot) + Translation;

    public PointD ApplyVector(PointD vector) => new(
        (vector.X * Cosine) - (vector.Y * Sine),
        (vector.X * Sine) + (vector.Y * Cosine));

    public PointD InverseApply(PointD point)
    {
        if (AngleDegrees % 360 == 0)
        {
            return point - Translation;
        }

        // 역변환은 이동을 먼저 빼고 반대 각도로 회전한다. 원래 변환의 역순이어야 한다.
        var relative = point - Translation - Pivot;
        return Pivot + new PointD(
            (relative.X * Cosine) + (relative.Y * Sine),
            (-relative.X * Sine) + (relative.Y * Cosine));
    }

    public BoundsD TransformBounds(BoundsD bounds) => Enclose(Apply(new(bounds.Left, bounds.Top)), Apply(new(bounds.Right, bounds.Top)),
        Apply(new(bounds.Right, bounds.Bottom)), Apply(new(bounds.Left, bounds.Bottom)));

    public BoundsD InverseTransformBounds(BoundsD bounds) => Enclose(InverseApply(new(bounds.Left, bounds.Top)), InverseApply(new(bounds.Right, bounds.Top)),
        InverseApply(new(bounds.Right, bounds.Bottom)), InverseApply(new(bounds.Left, bounds.Bottom)));

    private static BoundsD Enclose(PointD a, PointD b, PointD c, PointD d) => new(
        Math.Min(Math.Min(a.X, b.X), Math.Min(c.X, d.X)), Math.Min(Math.Min(a.Y, b.Y), Math.Min(c.Y, d.Y)),
        Math.Max(Math.Max(a.X, b.X), Math.Max(c.X, d.X)), Math.Max(Math.Max(a.Y, b.Y), Math.Max(c.Y, d.Y)));
}
