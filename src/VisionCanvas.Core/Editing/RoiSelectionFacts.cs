using System.Collections.Immutable;

namespace VisionCanvas.Core.Editing;

/// <summary>Shared immutable selection metadata; view-only changes reuse the same evaluated facts.</summary>
public sealed class RoiSelectionFacts
{
    private readonly Lazy<bool> _hasMultipleRoots;
    private readonly Lazy<bool> _canGroup;

    public Roi? First
    {
        get;
    }

    public bool HasGroup
    {
        get;
    }

    public bool HasMultipleRoots => _hasMultipleRoots.Value;
    public bool CanGroup => _canGroup.Value;

    public bool HasMixedFilters
    {
        get;
    }

    public ImmutableArray<Roi> RegisteredFilters
    {
        get;
    }

    internal RoiSelectionFacts(ImmutableDictionary<Guid, Roi> rois, IReadOnlySet<Guid> selection, RoiSnapshot snapshot)
    {
        // Command availability shares this result across navigation without adding work to unrelated facts.
        _hasMultipleRoots = new(() => FindMultipleRoots(rois, selection));
        _canGroup = new(() => HasMultipleRoots && RoiGroupEditor.CanGroup(snapshot,
            RoiHierarchy.EffectiveRoots(rois, selection).ToArray()));
        var registered = ImmutableArray.CreateBuilder<Roi>();
        FilterSettings? firstSettings = null;
        // Large partial selections scan immutable values once instead of performing a tree lookup
        // for every randomly ordered selected ID. Small selections keep the direct lookup path.
        var selectedRois = selection.Count == rois.Count ? rois.Values
            : selection.Count > rois.Count / 8 ? rois.Values.Where(roi => selection.Contains(roi.Id))
            : selection.Select(id => rois[id]);
        foreach (var roi in selectedRois)
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

    private static bool FindMultipleRoots(ImmutableDictionary<Guid, Roi> rois, IReadOnlySet<Guid> selection)
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
