namespace IndustrialVision.Filters.Core;

public sealed record Morphology : FilterSpec
{
    public override int Radius
    {
        get;
    }
    public MorphologyKind Kind
    {
        get;
    }
    public int Halo => Kind is MorphologyKind.Erosion or MorphologyKind.Dilation ? Radius : checked(2 * Radius);

    public Morphology(MorphologyKind kind, int radius)
    {
        if (!Enum.IsDefined(kind) || radius < 1 || radius > 4096)
        {
            throw new ArgumentOutOfRangeException(nameof(radius));
        }
        Kind = kind;
        Radius = radius;
    }
}
