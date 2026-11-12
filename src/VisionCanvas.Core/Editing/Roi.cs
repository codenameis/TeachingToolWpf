using System.Collections.Immutable;
using VisionGeometry;

namespace VisionCanvas.Core.Editing;

public sealed record Roi(Guid Id, string Name, RoiGeometry Geometry, Guid? ParentId, bool IsGroup, long Order)
{
    public const int MaximumNameLength = 256;

    public ImmutableArray<MaskRegion> Masks { get; init; } = [];

    public RoiSearchRange? SearchRange
    {
        get; init;
    }

    public RoiOcvSettings Ocv { get; init; } = RoiOcvSettings.Empty;

    public bool IsNameVisible { get; init; } = true;

    public FilterSettings Filter
    {
        get;
        init;
    } = new(RoiFilterKind.None);

    public ImmutableArray<FilterSettings> FilterPipeline { get; init; } = [];

    public ImmutableArray<FilterSettings> EffectiveFilterPipeline =>
        !FilterPipeline.IsDefaultOrEmpty
            ? FilterPipeline
            : (Filter.Kind != RoiFilterKind.None && Filter.IsEnabled ? [Filter] : []);

    public bool HasActiveFilter =>
        !FilterPipeline.IsDefaultOrEmpty
            ? FilterPipeline.Any(static s => s.IsEnabled && s.Kind != RoiFilterKind.None)
            : (Filter.IsEnabled && Filter.Kind != RoiFilterKind.None);

    public bool Equals(Roi? other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (other is null)
        {
            return false;
        }

        return Id == other.Id
            && Name == other.Name
            && Geometry == other.Geometry
            && ParentId == other.ParentId
            && IsGroup == other.IsGroup
            && Order == other.Order
            && Masks.AsSpan().SequenceEqual(other.Masks.AsSpan())
            && SearchRange == other.SearchRange
            && Ocv == other.Ocv
            && IsNameVisible == other.IsNameVisible
            && Filter == other.Filter
            && FilterPipeline.AsSpan().SequenceEqual(other.FilterPipeline.AsSpan());
    }

    public override int GetHashCode()
        => HashCode.Combine(Id, Name, Geometry, ParentId, IsGroup, Order, Filter, Ocv);
}
