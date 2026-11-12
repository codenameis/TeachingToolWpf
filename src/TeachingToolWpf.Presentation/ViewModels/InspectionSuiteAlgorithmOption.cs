using TeachingToolWpf.Commands;
using TeachingToolWpf.Imaging.Matching;

namespace TeachingToolWpf.ViewModels;

/// <summary>
/// 검사 스위트 알고리즘 콤보박스 및 카테고리 버튼 바인딩용 항목 모델.
/// </summary>
public sealed record InspectionSuiteAlgorithmOption(
    InspectionSuiteAlgorithmKind Kind,
    string Name,
    InspectionSuiteCategory Category = InspectionSuiteCategory.All,
    string ShortPillLabel = "")
{
    public RelayCommand? SelectCommand { get; init; }
}
