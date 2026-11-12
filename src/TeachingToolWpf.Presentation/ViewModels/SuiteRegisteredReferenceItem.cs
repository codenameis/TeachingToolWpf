using TeachingToolWpf.Commands;

namespace TeachingToolWpf.ViewModels;

/// <summary>
/// 검사 스위트 기준(정상/불량) 또는 멀티파츠 풀에 등록된 ROI 요약 항목.
/// </summary>
public sealed record SuiteRegisteredReferenceItem(
    Guid RoiId,
    string Name,
    string RoleBadge,
    string RoleAccent,
    string GeometrySummary,
    string ChildTargetSummary,
    RelayCommand RemoveCommand);
