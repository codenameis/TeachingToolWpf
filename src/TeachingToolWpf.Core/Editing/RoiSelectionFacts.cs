using System.Collections.Immutable;

namespace TeachingToolWpf.Core.Editing;

/// <summary>Shared immutable selection metadata; view-only changes reuse the same evaluated facts.</summary>
public sealed class RoiSelectionFacts
{
    private readonly Lazy<bool> _hasMultipleRoots;

    public Roi? First
    {
        get;
    }

    public bool HasGroup
    {
        get;
    }

    public bool HasMultipleRoots => _hasMultipleRoots.Value;

    public bool HasMixedFilters
    {
        get;
    }

    public ImmutableArray<Roi> RegisteredFilters
    {
        get;
    }

    internal RoiSelectionFacts(ImmutableDictionary<Guid, Roi> rois, ImmutableHashSet<Guid> selection)
    {
        // Command availability shares this result across navigation without adding work to unrelated facts.
        _hasMultipleRoots = new(() => FindMultipleRoots(rois, selection));
        var registered = ImmutableArray.CreateBuilder<Roi>();
        FilterSettings? firstSettings = null;
        foreach (var roi in selection.Count == rois.Count ? rois.Values : selection.Select(id => rois[id]))
        {
            if (First is null || roi.Order < First.Order)
            {
                First = roi;
            }

            HasGroup |= roi.IsGroup;
            if (firstSettings is null)
            {
                firstSettings = roi.Filter;
            }
            else
            {
                HasMixedFilters |= firstSettings != roi.Filter;
            }

            if (roi.Filter.Kind != RoiFilterKind.None)
            {
                registered.Add(roi);
            }
        }
        registered.Sort(Comparer<Roi>.Create((a, b) => a.Order.CompareTo(b.Order)));
        RegisteredFilters = registered.ToImmutable();
    }

    private static bool FindMultipleRoots(ImmutableDictionary<Guid, Roi> rois, ImmutableHashSet<Guid> selection)
    {
        if (selection.Count < 2)
        {
            return false;
        }

        var roots = 0;
        if (selection.Count == rois.Count)
        {
            foreach (var roi in rois.Values)
            {
                if (roi.ParentId is null && ++roots == 2)
                {
                    return true;
                }
            }

            return false;
        }
        foreach (var id in selection)
        {
            var parent = rois[id].ParentId;
            while (parent is { } ancestor && !selection.Contains(ancestor))
            {
                parent = rois[ancestor].ParentId;
            }

            if (parent is null && ++roots == 2)
            {
                return true;
            }
        }
        return false;
    }
}
