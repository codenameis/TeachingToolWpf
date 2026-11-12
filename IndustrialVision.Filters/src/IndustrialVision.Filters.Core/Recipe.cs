namespace IndustrialVision.Filters.Core;

public sealed class Recipe
{
    public IReadOnlyList<FilterSpec> Filters
    {
        get;
    }
    public long Version
    {
        get;
    }

    public Recipe(long version, params FilterSpec[] filters)
    {
        if (version <= 0 || filters.Length == 0 || filters.Any(f => f is null))
        {
            throw new ArgumentException("A positive version and nonempty recipe are required.");
        }
        Filters = Array.AsReadOnly((FilterSpec[])filters.Clone());
        Version = version;
    }
}
