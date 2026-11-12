namespace IndustrialVision.Filters.Core;

public abstract record FilterSpec
{
    public abstract int Radius
    {
        get;
    }
}
