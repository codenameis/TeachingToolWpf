using System.Collections.Immutable;

namespace TeachingToolWpf.Core.Editing;

/// <summary>Shared immutable selection metadata; view-only changes reuse the same evaluated facts.</summary>
public sealed class RoiSelectionFacts
{
    public Roi? First { get; }
    public bool HasGroup { get; }
    public bool HasMixedFilters { get; }
    public ImmutableArray<Roi> RegisteredFilters { get; }
    internal RoiSelectionFacts(ImmutableDictionary<Guid, Roi> rois, ImmutableHashSet<Guid> selection)
    {
        var registered = ImmutableArray.CreateBuilder<Roi>();
        FilterSettings? firstSettings = null;
        foreach (var roi in selection.Count == rois.Count ? rois.Values : selection.Select(id => rois[id]))
        {
            if (First is null || roi.Order < First.Order) First = roi;
            HasGroup |= roi.IsGroup;
            if (firstSettings is null) firstSettings = roi.Filter;
            else HasMixedFilters |= firstSettings != roi.Filter;
            if (roi.Filter.Kind != RoiFilterKind.None) registered.Add(roi);
        }
        registered.Sort(Comparer<Roi>.Create((a, b) => a.Order.CompareTo(b.Order)));
        RegisteredFilters = registered.ToImmutable();
    }
}
