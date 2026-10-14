using System.Collections.Immutable;
using VisionCanvas.Core.Geometry;
using VisionGeometry;

namespace VisionCanvas.Core.Editing;

/// <summary>Derived immutable indexes shared by selection/view snapshots. No UI or GPU ownership.</summary>
public sealed partial class RoiSnapshot
{
    private readonly Lazy<RoiSpatialIndex> _spatial;
    private readonly Lazy<ImmutableDictionary<Guid, ImmutableArray<Guid>>> _children;
    private readonly Lazy<ImmutableDictionary<Guid, int>> _groupLevels;

    public ImmutableDictionary<Guid, Roi> Rois
    {
        get;
    }

    public RoiSpatialIndex Spatial => _spatial.Value;

    internal bool IsSpatialCreated => _spatial.IsValueCreated;

    public ImmutableDictionary<Guid, ImmutableArray<Guid>> Children => _children.Value;

    // 리프 ROI=0, ROI 그룹=1, 모듈 그룹=2, 루트 그룹=3. 탐색·형상 편집에서 재사용한다.
    public ImmutableDictionary<Guid, int> GroupLevels => _groupLevels.Value;

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
        _groupLevels = new(() => RoiHierarchy.BuildGroupLevels(rois));
        _spatial = new(() => new(rois.Values));
        _children = new(() => rois.Values.Where(r => r.ParentId.HasValue).GroupBy(r => r.ParentId!.Value)
            .ToImmutableDictionary(g => g.Key, g => g.Select(r => r.Id).ToImmutableArray()));
        _search = new(BuildSearchIndex);
    }

    internal RoiSnapshot(RoiSnapshot previous, ImmutableDictionary<Guid, Roi> rois, ImmutableArray<Guid> changedIds, RoiSnapshot? spatialSeed = null, bool refitSpatial = false, CancellationToken token = default)
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
        var groupKindChanged = false;
        var rebuildSpatial = false;
        foreach (var id in changedIds)
        {
            token.ThrowIfCancellationRequested();
            var old = oldRois.GetValueOrDefault(id);
            var next = rois.GetValueOrDefault(id);
            groupKindChanged |= old?.IsGroup != next?.IsGroup;
            rowsChanged = rowsChanged || old is null || next is null || old.Name != next.Name || old.ParentId != next.ParentId || old.Order != next.Order || old.IsGroup != next.IsGroup || old.Geometry.Shape.Kind != next.Geometry.Shape.Kind || (old.SearchRange is null) != (next.SearchRange is null);
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
        _groupLevels = topologyChanged || groupKindChanged
            ? new(() => RoiHierarchy.BuildGroupLevels(rois)) : previous._groupLevels;
        // Capture evaluated values, not previous factories: Undo/Redo must not retain a recursive lazy chain.
        var oldSpatial = rebuildSpatial && !refitSpatial ? null : seed.Spatial;
        var spatialChanges = geometryChanges?.ToArray();
        // 작업 전용 refit은 게시 전에 취소한다. 공유 Lazy에 취소 예외나 일시적 token을 보관하지 않는다.
        var refitted = refitSpatial && token.CanBeCanceled ? oldSpatial!.RefitGeometry(rois, token) : null;
        _spatial = new(() => refitSpatial ? refitted ?? oldSpatial!.RefitGeometry(rois) : rebuildSpatial ? new(rois.Values) :
            (spatialChanges!.Length == 0 ? oldSpatial! : oldSpatial!.WithChanges(spatialChanges)).WithValues(rois));
        if (!topologyChanged)
        {
            _children = previous._children;
            _search = CreateSearchIndex(previous);
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
        _search = CreateSearchIndex(previous);
    }

    private static bool RequiresRebuild(int changedCount, int totalCount)
        => changedCount > 4096 || (changedCount > 64 && changedCount > totalCount / 8);

    private readonly record struct ParentChange(Guid Id, Guid? OldParent, Guid? NewParent);
}
