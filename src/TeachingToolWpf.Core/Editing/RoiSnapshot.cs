using System.Collections.Immutable;
using TeachingToolWpf.Core.Geometry;

namespace TeachingToolWpf.Core.Editing;

/// <summary>Derived immutable indexes shared by selection/view snapshots. No UI or GPU ownership.</summary>
public sealed class RoiSnapshot
{
    private readonly Lazy<RoiSpatialIndex> spatial;
    private readonly Lazy<ImmutableDictionary<Guid, ImmutableArray<Guid>>> children;
    public ImmutableDictionary<Guid, Roi> Rois { get; }
    public RoiSpatialIndex Spatial => spatial.Value;
    public ImmutableDictionary<Guid, ImmutableArray<Guid>> Children => children.Value;
    public ImmutableDictionary<Guid, Roi>? PreviousRois { get; }
    public ImmutableArray<Guid> ChangedIds { get; }
    public object RowVersion { get; }
    public RoiSnapshot(ImmutableDictionary<Guid, Roi> rois)
    {
        Rois = rois;
        RowVersion = new();
        spatial = new(() => new(rois.Values));
        children = new(() => rois.Values.Where(r => r.ParentId.HasValue).GroupBy(r => r.ParentId!.Value)
            .ToImmutableDictionary(g => g.Key, g => g.Select(r => r.Id).ToImmutableArray()));
    }
    internal RoiSnapshot(RoiSnapshot previous, ImmutableDictionary<Guid, Roi> rois, ImmutableArray<Guid> changedIds, RoiSnapshot? spatialSeed = null)
    {
        Rois = rois; PreviousRois = previous.Rois; ChangedIds = changedIds;
        RowVersion = changedIds.Any(id =>
        {
            var old = previous.Rois.GetValueOrDefault(id); var next = rois.GetValueOrDefault(id);
            return old is null || next is null || old.Name != next.Name || old.ParentId != next.ParentId || old.Order != next.Order || old.IsGroup != next.IsGroup;
        }) ? new() : previous.RowVersion;
        var oldRois = previous.Rois;
        var bulk = changedIds.Length > 4096 || changedIds.Length > 64 && changedIds.Length > rois.Count / 8;
        var seed = spatialSeed ?? previous;
        var geometryChanges = changedIds.Where(id => seed.Rois.GetValueOrDefault(id)?.Geometry != rois.GetValueOrDefault(id)?.Geometry).ToArray();
        var rebuildSpatial = geometryChanges.Length > 4096 || geometryChanges.Length > 64 && geometryChanges.Length > rois.Count / 8;
        // Capture evaluated values, not previous factories: Undo/Redo must not retain a recursive lazy chain.
        var oldSpatial = rebuildSpatial ? null : seed.Spatial;
        spatial = new(() => rebuildSpatial ? new(rois.Values) :
            (geometryChanges.Length == 0 ? oldSpatial! : oldSpatial!.WithChanges(geometryChanges.Select(id => (id, rois.GetValueOrDefault(id))))).WithValues(rois));
        var topologyChanged = changedIds.Any(id => oldRois.GetValueOrDefault(id)?.ParentId != rois.GetValueOrDefault(id)?.ParentId);
        if (!topologyChanged) { children = previous.children; return; }
        var oldChildren = bulk ? null : previous.Children;
        children = new(() =>
        {
            if (bulk) return rois.Values.Where(r => r.ParentId.HasValue).GroupBy(r => r.ParentId!.Value)
                .ToImmutableDictionary(g => g.Key, g => g.Select(r => r.Id).ToImmutableArray());
            var result = oldChildren!.ToBuilder();
            foreach (var id in changedIds)
            {
                var oldParent = oldRois.GetValueOrDefault(id)?.ParentId;
                var newParent = rois.GetValueOrDefault(id)?.ParentId;
                if (oldParent == newParent) continue;
                if (oldParent is { } before && result.TryGetValue(before, out var siblings))
                {
                    var remaining = siblings.Remove(id);
                    if (remaining.IsEmpty) result.Remove(before); else result[before] = remaining;
                }
                if (newParent is { } after) result[after] = result.GetValueOrDefault(after, []).Add(id);
            }
            return result.ToImmutable();
        });
    }
}
