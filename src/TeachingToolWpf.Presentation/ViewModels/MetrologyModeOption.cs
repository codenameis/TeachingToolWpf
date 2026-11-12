using TeachingToolWpf.Imaging.Matching;

namespace TeachingToolWpf.ViewModels;

/// <summary>
/// 캘리퍼 계측 모드 선택 옵션.
/// </summary>
public sealed record MetrologyModeOption(MetrologyMeasurementMode Mode, string Name);
