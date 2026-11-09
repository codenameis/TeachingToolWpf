using System.Collections.Immutable;
using VisionCanvas.Core.Geometry;

namespace VisionCanvas.Core.Editing;

public sealed partial class RoiSnapshot
{
    private readonly Lazy<ImmutableArray<RoiScopePartition>> _alignmentPartitions;

    /// <summary>
    /// 보정 범위(그룹 ROI는 자기 자신, 리프 ROI는 ParentId)별로 분할된 불변 공간 인덱스 목록.
    /// 문서 구조가 바뀔 때만 지연 생성되며, 보정값(X, Y, Theta) 변경 시에는 재구축하지 않는다.
    /// </summary>
    public ImmutableArray<RoiScopePartition> AlignmentPartitions => _alignmentPartitions.Value;

    private Guid? ResolveGoverningScopeId(Roi roi)
        => (roi.IsGroup || Children.ContainsKey(roi.Id)) ? roi.Id : roi.ParentId;

    private ImmutableArray<RoiScopePartition> BuildAlignmentPartitions()
    {
        if (Rois.IsEmpty)
        {
            return [];
        }

        var groups = new Dictionary<Guid, List<Roi>>();
        List<Roi>? nullScopeRois = null;

        foreach (var roi in Rois.Values)
        {
            if (ResolveGoverningScopeId(roi) is not { } scopeId)
            {
                nullScopeRois ??= [];
                nullScopeRois.Add(roi);
                continue;
            }

            if (!groups.TryGetValue(scopeId, out var list))
            {
                list = [];
                groups.Add(scopeId, list);
            }

            list.Add(roi);
        }

        var builder = ImmutableArray.CreateBuilder<RoiScopePartition>((nullScopeRois is null ? 0 : 1) + groups.Count);
        if (nullScopeRois is not null)
        {
            builder.Add(CreatePartition(null, nullScopeRois));
        }

        foreach (var (scopeId, list) in groups.OrderBy(pair => Rois.TryGetValue(pair.Key, out var r) ? r.Order : long.MaxValue))
        {
            builder.Add(CreatePartition(scopeId, list));
        }

        return builder.ToImmutable();
    }

    private RoiScopePartition CreatePartition(Guid? scopeId, List<Roi> rois)
    {
        var spatial = new RoiSpatialIndex(rois);
        List<Roi>? searchRois = null;
        for (var index = 0; index < rois.Count; index++)
        {
            if (SearchRoi(rois[index].Id) is { } expanded)
            {
                searchRois ??= [];
                searchRois.Add(expanded);
            }
        }

        var hasSearch = searchRois is { Count: > 0 };
        var searchSpatial = hasSearch ? new RoiSpatialIndex(searchRois!) : new RoiSpatialIndex([]);
        return new RoiScopePartition(scopeId, spatial, searchSpatial, hasSearch);
    }

    private Lazy<ImmutableArray<RoiScopePartition>> CreateAlignmentPartitions(
        RoiSnapshot previous,
        bool topologyChanged,
        bool groupKindChanged,
        bool rebuildSpatial,
        bool refitSpatial,
        List<(Guid Id, Roi? Value)>? geometryChanges)
    {
        var hasGeometryChange = rebuildSpatial || refitSpatial || (geometryChanges is { Count: > 0 });
        if (!topologyChanged && !groupKindChanged && !hasGeometryChange && previous._alignmentPartitions.IsValueCreated)
        {
            var cached = previous._alignmentPartitions.Value;
            return new(() => cached);
        }

        return new(BuildAlignmentPartitions);
    }
}
