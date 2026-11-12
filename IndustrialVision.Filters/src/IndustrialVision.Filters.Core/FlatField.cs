namespace IndustrialVision.Filters.Core;

public sealed record FlatField : FilterSpec
{
    public CalibrationMap Map
    {
        get;
    }
    public CalibrationMap? Dark
    {
        get;
    }
    public FlatFieldKind Kind
    {
        get;
    }
    public float Scale
    {
        get;
    }
    public float MinimumDenominator
    {
        get;
    }
    public override int Radius => 0;

    public FlatField(CalibrationMap map, FlatFieldKind kind = FlatFieldKind.RawReference,
        CalibrationMap? dark = null, float scale = 1, float minimumDenominator = 0.000001f)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (!Enum.IsDefined(kind) || !float.IsFinite(scale) || scale <= 0 ||
            !float.IsFinite(minimumDenominator) || minimumDenominator <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(scale));
        }
        Map = map;
        Dark = dark;
        Kind = kind;
        Scale = scale;
        MinimumDenominator = minimumDenominator;
    }
}
