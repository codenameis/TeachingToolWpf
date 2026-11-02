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

    public ImmutableDictionary<Guid, Roi> Rois { get; }

    public RoiSpatialIndex Spatial => _spatial.Value;

    internal bool IsSpatialCreated => _spatial.IsValueCreated;

    public ImmutableDictionary<Guid, ImmutableArray<Guid>> Children => _children.Value;

    // 리프 ROI=0, ROI 그룹=1, 모듈 그룹=2, 루트 그룹=3. 탐색·형상 편집에서 재사용한다.
    public ImmutableDictionary<Guid, int> GroupLevels => _groupLevels.Value;

    public ImmutableDictionary<Guid, Roi>? PreviousRois { get; }

    public ImmutableArray<Guid> ChangedIds { get; }

    public object RowVersion { get; }

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
        // Refit은 모든 기존 공간 분할을 갱신하므로 별도의 변경 목록이 필요 없다.
        List<(Guid Id, Roi? Value)>? geometryChanges = refitSpatial ? null : [];
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
            rowsChanged = rowsChanged || HasRowMetadataChanged(old, next);
            if (old?.ParentId != next?.ParentId)
            {
                topologyChanged = true;
                parentChanges?.Add(new(id, old?.ParentId, next?.ParentId));
            }
            if (!rebuildSpatial && !refitSpatial)
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
        _spatial = BuildOrUpdateSpatialIndex(seed, rois, geometryChanges, rebuildSpatial, refitSpatial, token);
        _children = BuildOrUpdateChildrenIndex(previous, rois, parentChanges, topologyChanged, bulk);
        _search = CreateSearchIndex(previous);
    }

    private static bool HasRowMetadataChanged(Roi? oldRoi, Roi? nextRoi)
    {
        if (oldRoi is null || nextRoi is null)
        {
            return true;
        }

        return oldRoi.Name != nextRoi.Name
            || oldRoi.ParentId != nextRoi.ParentId
            || oldRoi.Order != nextRoi.Order
            || oldRoi.IsGroup != nextRoi.IsGroup
            || oldRoi.Geometry.Shape.Kind != nextRoi.Geometry.Shape.Kind
            || (oldRoi.SearchRange is null) != (nextRoi.SearchRange is null);
    }

    private static Lazy<RoiSpatialIndex> BuildOrUpdateSpatialIndex(
        RoiSnapshot seed,
        ImmutableDictionary<Guid, Roi> rois,
        List<(Guid Id, Roi? Value)>? geometryChanges,
        bool rebuildSpatial,
        bool refitSpatial,
        CancellationToken token)
    {
        // Capture evaluated values, not previous factories: Undo/Redo must not retain a recursive lazy chain.
        var oldSpatial = rebuildSpatial && !refitSpatial ? null : seed.Spatial;
        if (refitSpatial)
        {
            // 작업 전용 refit은 게시 전에 취소한다. 공유 Lazy에 취소 예외나 일시적 token을 보관하지 않는다.
            var refitted = token.CanBeCanceled ? oldSpatial!.RefitGeometry(rois, token) : null;
            return new(() => refitted ?? oldSpatial!.RefitGeometry(rois));
        }

        if (rebuildSpatial)
        {
            return new(() => new(rois.Values));
        }

        var spatialChanges = geometryChanges!.ToArray();
        return new(() =>
        {
            var updated = spatialChanges.Length == 0 ? oldSpatial! : oldSpatial!.WithChanges(spatialChanges);
            return updated.WithValues(rois);
        });
    }

    private static Lazy<ImmutableDictionary<Guid, ImmutableArray<Guid>>> BuildOrUpdateChildrenIndex(
        RoiSnapshot previous,
        ImmutableDictionary<Guid, Roi> rois,
        List<ParentChange>? parentChanges,
        bool topologyChanged,
        bool bulk)
    {
        if (!topologyChanged)
        {
            return previous._children;
        }

        var oldChildren = bulk ? null : previous.Children;
        var topologyChanges = parentChanges?.ToArray();
        return new(() =>
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
