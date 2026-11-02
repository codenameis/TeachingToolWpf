using VisionGeometry;

namespace VisionCanvas.Core.Editing;

/// <summary>검사 도구가 쓰는 단일 선택 ROI의 기하. 그룹·다중 선택은 검사 대상이 아니다.</summary>
/// <remarks>
/// <see cref="Selection"/>은 편집한 도형 그대로이고, <see cref="SearchSelection"/>만 검색 여유를 포함한다.
/// <see cref="Exclusion"/>은 마스크가 있을 때만 만들며 기준 도형은 항상 <see cref="Selection"/>이다.
/// </remarks>
public readonly struct InspectionSelection
{
    private InspectionSelection(RoiGeometry selection, RoiGeometry searchSelection, RoiExclusion? exclusion, Guid roiId)
    {
        Selection = selection;
        SearchSelection = searchSelection;
        Exclusion = exclusion;
        RoiId = roiId;
    }

    /// <summary>선택 도형. 검색 여유를 포함하지 않는다.</summary>
    public RoiGeometry? Selection { get; }

    /// <summary>검색 여유를 포함한 도형. 여유가 없으면 <see cref="Selection"/>과 같은 인스턴스다.</summary>
    public RoiGeometry? SearchSelection { get; }

    /// <summary>마스크 제외 영역. 마스크가 없으면 null.</summary>
    public RoiExclusion? Exclusion { get; }

    /// <summary>학습·검사에 쓰는 ROI ID. 대상이 없으면 null.</summary>
    public Guid? RoiId { get; }

    /// <summary>문서의 단일 선택을 검사 대상으로 해석한다.</summary>
    /// <remarks>검색 여유나 마스크가 없으면 힙 할당이 없다. 프레임마다 호출해도 된다.</remarks>
    public static InspectionSelection Resolve(DocumentState state)
    {
        // 선택은 0개·1개·여러 개 중 하나다. 검사 도구는 정확히 1개일 때만 대상을 정한다.
        if (!state.Selection.TryGetOnly(out var roiId))
        {
            return default;
        }

        var roi = state.Rois[roiId];
        if (state.IsGroupRoi(roiId))
        {
            return default;
        }

        var selection = roi.Geometry;
        var searchSelection = roi.SearchRange?.Expand(selection) ?? selection;
        var exclusion = roi.Masks.IsEmpty ? null : new RoiExclusion(selection, roi.Masks);
        return new(selection, searchSelection, exclusion, roiId);
    }
}
