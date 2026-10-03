using System.Collections.Immutable;
using TeachingToolWpf.Core.Geometry;

namespace TeachingToolWpf.Core.Editing;

/// <summary>Derived immutable indexes shared by selection/view snapshots. No UI or GPU ownership.</summary>
public sealed class RoiSnapshot
{
    private readonly Lazy<RoiSpatialIndex> _spatial;
    private readonly Lazy<ImmutableDictionary<Guid, ImmutableArray<Guid>>> _children;

    public ImmutableDictionary<Guid, Roi> Rois
    {
        get;
    }

    public RoiSpatialIndex Spatial => _spatial.Value;

    public ImmutableDictionary<Guid, ImmutableArray<Guid>> Children => _children.Value;

    public ImmutableDictionary<Guid, Roi>? PreviousRois
    {
        get;
    }

    public ImmutableArray<Guid> ChangedIds
    {
        get;
    }

    public object RowVersion
    {
        get;
    }

    internal bool IsRigidUpdate
    {
        get;
    }

    public RoiSnapshot(ImmutableDictionary<Guid, Roi> rois)
    {
        Rois = rois;
        RowVersion = new();
        _spatial = new(() => new(rois.Values));
        _children = new(() => rois.Values.Where(r => r.ParentId.HasValue).GroupBy(r => r.ParentId!.Value)
            .ToImmutableDictionary(g => g.Key, g => g.Select(r => r.Id).ToImmutableArray()));
    }

    internal RoiSnapshot(RoiSnapshot previous, ImmutableDictionary<Guid, Roi> rois, ImmutableArray<Guid> changedIds, RoiSnapshot? spatialSeed = null, bool refitSpatial = false)
    {
        Rois = rois;
        PreviousRois = previous.Rois;
        ChangedIds = changedIds;
        IsRigidUpdate = refitSpatial;
        var oldRois = previous.Rois;
        var bulk = RequiresRebuild(changedIds.Length, rois.Count);
        var seed = spatialSeed ?? previous;
        var seedIsPrevious = ReferenceEquals(seed.Rois, oldRois);
        List<(Guid Id, Roi? Value)>? geometryChanges = [];
        List<ParentChange>? parentChanges = bulk ? null : [];
        var rowsChanged = false;
        var topologyChanged = false;
        var rebuildSpatial = false;
        foreach (var id in changedIds)
        {
            var old = oldRois.GetValueOrDefault(id);
            var next = rois.GetValueOrDefault(id);
            rowsChanged = rowsChanged || old is null || next is null || old.Name != next.Name || old.ParentId != next.ParentId || old.Order != next.Order || old.IsGroup != next.IsGroup || old.Geometry.Shape.Kind != next.Geometry.Shape.Kind;
            if (old?.ParentId != next?.ParentId)
            {
                topologyChanged = true;
                parentChanges?.Add(new(id, old?.ParentId, next?.ParentId));
            }
            if (!rebuildSpatial)
            {
                var indexed = seedIsPrevious ? old : seed.Rois.GetValueOrDefault(id);
                if (indexed?.Geometry != next?.Geometry || indexed?.Order != next?.Order)
                {
                    geometryChanges!.Add((id, next));
                    if (RequiresRebuild(geometryChanges.Count, rois.Count))
                    {
                        rebuildSpatial = true;
                        geometryChanges = null;
                    }
                }
            }
            // Once every index needs rebuilding, later values cannot change the decision.
            if (bulk && rowsChanged && topologyChanged && rebuildSpatial)
            {
                break;
            }
        }
        RowVersion = rowsChanged ? new() : previous.RowVersion;
        // Capture evaluated values, not previous factories: Undo/Redo must not retain a recursive lazy chain.
        var oldSpatial = rebuildSpatial && !refitSpatial ? null : seed.Spatial;
        var spatialChanges = geometryChanges?.ToArray();
        _spatial = new(() => refitSpatial ? oldSpatial!.RefitGeometry(rois) : rebuildSpatial ? new(rois.Values) :
            (spatialChanges!.Length == 0 ? oldSpatial! : oldSpatial!.WithChanges(spatialChanges)).WithValues(rois));
        if (!topologyChanged)
        {
            _children = previous._children;
            return;
        }
        var oldChildren = bulk ? null : previous.Children;
        var topologyChanges = parentChanges?.ToArray();
        _children = new(() =>
        {
            if (bulk)
            {
                return rois.Values.Where(r => r.ParentId.HasValue).GroupBy(r => r.ParentId!.Value)
                .ToImmutableDictionary(g => g.Key, g => g.Select(r => r.Id).ToImmutableArray());
            }

            var result = oldChildren!.ToBuilder();
            foreach (var (id, oldParent, newParent) in topologyChanges!)
            {
                if (oldParent is { } before && result.TryGetValue(before, out var siblings))
                {
                    var remaining = siblings.Remove(id);
                    if (remaining.IsEmpty)
                    {
                        result.Remove(before);
                    }
                    else
                    {
                        result[before] = remaining;
                    }
                }
                if (newParent is { } after)
                {
                    result[after] = result.GetValueOrDefault(after, []).Add(id);
                }
            }
            return result.ToImmutable();
        });
    }

    private static bool RequiresRebuild(int changedCount, int totalCount)
        => changedCount > 4096 || (changedCount > 64 && changedCount > totalCount / 8);

    private readonly record struct ParentChange(Guid Id, Guid? OldParent, Guid? NewParent);
}
