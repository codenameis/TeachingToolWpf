using System.Collections.Immutable;
using VisionGeometry;

namespace VisionCanvas.Core.Editing;

/// <summary>검사 도구가 쓰는 선택 ROI의 기하. 단일 선택 해석과 다중·미선택 리프 ROI 일괄 해석을 제공한다.</summary>
/// <remarks>
/// <see cref="Selection"/>은 편집한 도형 그대로이고, <see cref="SearchSelection"/>만 검색 여유를 포함한다.
/// <see cref="Exclusion"/>은 마스크가 있을 때만 만들며 기준 도형은 항상 <see cref="Selection"/>이다.
/// </remarks>
public readonly struct InspectionSelection
{
    private InspectionSelection(RoiGeometry selection, RoiGeometry searchSelection, RoiExclusion? exclusion, Guid roiId, string roiName)
    {
        Selection = selection;
        SearchSelection = searchSelection;
        Exclusion = exclusion;
        RoiId = roiId;
        RoiName = roiName;
    }

    /// <summary>선택 도형. 검색 여유를 포함하지 않는다.</summary>
    public RoiGeometry? Selection { get; }

    /// <summary>검색 여유를 포함한 도형. 여유가 없으면 <see cref="Selection"/>과 같은 인스턴스다.</summary>
    public RoiGeometry? SearchSelection { get; }

    /// <summary>마스크 제외 영역. 마스크가 없으면 null.</summary>
    public RoiExclusion? Exclusion { get; }

    /// <summary>학습·검사에 쓰는 ROI ID. 대상이 없으면 null.</summary>
    public Guid? RoiId { get; }

    /// <summary>대상 ROI의 표시 이름.</summary>
    public string RoiName { get; }

    /// <summary>문서의 단일 선택을 검사 대상으로 해석한다.</summary>
    /// <remarks>검색 여유나 마스크가 없으면 힙 할당이 없다. 프레임마다 호출해도 된다.</remarks>
    public static InspectionSelection Resolve(DocumentState state, RoiTransformPreview? transformPreview = null)
    {
        // 선택은 0개·1개·여러 개 중 하나다. 단일 학습/편집은 정확히 1개 리프 ROI일 때만 대상을 정한다.
        if (!state.Selection.TryGetOnly(out var roiId))
        {
            return default;
        }

        if (!state.Rois.TryGetValue(roiId, out var roi) || state.IsGroupRoi(roiId))
        {
            return default;
        }

        return CreateFromRoi(roi, transformPreview);
    }

    /// <summary>
    /// 전체 도형·변환 배열을 할당하지 않고 대상 리프 ROI 개수만 반환한다.
    /// 그룹 계층이 없는 문서(반복 배치 포함)에서는 O(1)에 반환한다.
    /// </summary>
    public static int CountTargetLeafRois(DocumentState state)
    {
        if (state.Rois.IsEmpty)
        {
            return 0;
        }

        if (state.Selection.Count == 1)
        {
            return state.Selection.TryGetOnly(out var roiId)
                && state.Rois.ContainsKey(roiId)
                && !state.IsGroupRoi(roiId)
                ? 1
                : 0;
        }

        if (state.Snapshot.Children.IsEmpty && state.Snapshot.GroupLevels.IsEmpty)
        {
            return state.Selection.Count > 1 ? state.Selection.Count : state.Rois.Count;
        }

        if (state.Selection.Count > 1)
        {
            var selectedCount = 0;
            foreach (var roiId in state.Selection)
            {
                if (state.Rois.ContainsKey(roiId) && !state.IsGroupRoi(roiId))
                {
                    selectedCount++;
                }
            }

            return selectedCount;
        }

        var leafCount = 0;
        foreach (var roiId in state.Rois.Keys)
        {
            if (!state.IsGroupRoi(roiId))
            {
                leafCount++;
            }
        }

        return leafCount;
    }

    /// <summary>
    /// 다중 ROI 선택 시 선택된 모든 리프 ROI를, 아무것도 선택되지 않았을 때는 문서 내 모든 리프 ROI를 순서대로 반환한다.
    /// 단일 그룹 ROI만 선택된 경우에는 기존 계약을 유지하기 위해 빈 배열을 반환한다.
    /// </summary>
    public static ImmutableArray<InspectionSelection> ResolveTargetLeafRois(DocumentState state, RoiTransformPreview? transformPreview = null)
    {
        if (state.Rois.IsEmpty)
        {
            return [];
        }

        if (state.Selection.Count == 1)
        {
            var single = Resolve(state, transformPreview);
            return single.RoiId is not null ? [single] : [];
        }

        if (state.Selection.Count > 1)
        {
            if (state.Selection.Count * 8 < state.Rois.Count)
            {
                var selectedRois = new List<Roi>(state.Selection.Count);
                foreach (var roiId in state.Selection)
                {
                    if (state.Rois.TryGetValue(roiId, out var roi) && !state.IsGroupRoi(roiId))
                    {
                        selectedRois.Add(roi);
                    }
                }

                selectedRois.Sort(static (a, b) => a.Order.CompareTo(b.Order));
                var smallBuilder = ImmutableArray.CreateBuilder<InspectionSelection>(selectedRois.Count);
                for (var i = 0; i < selectedRois.Count; i++)
                {
                    smallBuilder.Add(CreateFromRoi(selectedRois[i], transformPreview));
                }

                return smallBuilder.MoveToImmutable();
            }

            var builder = ImmutableArray.CreateBuilder<InspectionSelection>(state.Selection.Count);
            foreach (var roi in state.Rois.Values.OrderBy(r => r.Order))
            {
                if (state.Selection.Contains(roi.Id) && !state.IsGroupRoi(roi.Id))
                {
                    builder.Add(CreateFromRoi(roi, transformPreview));
                }
            }

            return builder.ToImmutable();
        }

        var allLeaves = ImmutableArray.CreateBuilder<InspectionSelection>(state.Rois.Count);
        foreach (var roi in state.Rois.Values.OrderBy(r => r.Order))
        {
            if (!state.IsGroupRoi(roi.Id))
            {
                allLeaves.Add(CreateFromRoi(roi, transformPreview));
            }
        }

        return allLeaves.ToImmutable();
    }

    private static InspectionSelection CreateFromRoi(Roi roi, RoiTransformPreview? transformPreview)
    {
        var selection = transformPreview?.Geometry(roi) ?? roi.Geometry;
        var searchSelection = roi.SearchRange?.Expand(selection) ?? selection;
        var exclusion = roi.Masks.IsEmpty ? null : new RoiExclusion(selection, roi.Masks);
        return new(selection, searchSelection, exclusion, roi.Id, roi.Name);
    }
}
