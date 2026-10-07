using System.Collections.Immutable;
using VisionCanvas.Core.Geometry;

namespace VisionCanvas.Core.Editing;

public sealed partial class RoiSnapshot
{
    private readonly Lazy<(ImmutableDictionary<Guid, Roi> Rois, RoiSpatialIndex Spatial)> _search;

    // 검색 경계만 화면에 들어오는 경우도 조회한다. 실제 ROI의 hit/변형 제한 색인과 분리한다.
    public RoiSpatialIndex SearchSpatial => _search.Value.Spatial;

    private Roi? SearchRoi(Guid id)
        => Rois.TryGetValue(id, out var roi) && roi.SearchRange is { } range && !roi.IsGroup && !Children.ContainsKey(id)
            ? roi with
            {
                Geometry = range.Expand(roi.Geometry)
            } : null;

    private (ImmutableDictionary<Guid, Roi>, RoiSpatialIndex) BuildSearchIndex()
    {
        var values = Rois.Values.Where(roi => roi.SearchRange is not null).Select(roi => SearchRoi(roi.Id))
            .OfType<Roi>().ToImmutableDictionary(roi => roi.Id);
        return (values, new(values.Values));
    }

    private Lazy<(ImmutableDictionary<Guid, Roi>, RoiSpatialIndex)> CreateSearchIndex(RoiSnapshot previous)
    {
        if (!previous._search.IsValueCreated || RequiresRebuild(ChangedIds.Length, Rois.Count))
        {
            return new(BuildSearchIndex);
        }
        // 이전 Lazy/문서를 연쇄 보관하지 않고 평가된 불변 값만 잡는다.
        var old = previous._search.Value;
        var candidates = ChangedIds.ToHashSet();
        foreach (var id in ChangedIds)
        {
            if (previous.Rois.GetValueOrDefault(id)?.ParentId is { } oldParent)
            {
                candidates.Add(oldParent);
            }
            if (Rois.GetValueOrDefault(id)?.ParentId is { } newParent)
            {
                candidates.Add(newParent);
            }
        }
        return new(() =>
        {
            var values = old.Rois.ToBuilder();
            var changes = new List<(Guid, Roi?)>();
            foreach (var id in candidates)
            {
                var next = SearchRoi(id);
                var prior = old.Rois.GetValueOrDefault(id);
                if (next == prior)
                {
                    continue;
                }
                if (next is null)
                {
                    values.Remove(id);
                }
                else
                {
                    values[id] = next;
                }
                if (next?.Geometry != prior?.Geometry || next?.Order != prior?.Order)
                {
                    changes.Add((id, next));
                }
            }
            var rois = values.ToImmutable();
            return (rois, (changes.Count == 0 ? old.Spatial : old.Spatial.WithChanges(changes)).WithValues(rois));
        });
    }
}
