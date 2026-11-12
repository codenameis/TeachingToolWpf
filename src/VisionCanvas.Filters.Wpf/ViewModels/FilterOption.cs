using VisionCanvas.Core.Editing;

namespace VisionCanvas.Filters.Wpf.ViewModels;

/// <summary>UI 콤보박스에 표시되는 ROI 필터 종류와 설명 항목.</summary>
public sealed record FilterOption(RoiFilterKind Kind, string Label)
{
    public override string ToString() => Label;
}
