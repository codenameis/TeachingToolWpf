using TeachingToolWpf.Imaging.Matching;

namespace TeachingToolWpf.ViewModels;

/// <summary>
/// 캘리퍼 에지 극성 모드 선택 옵션.
/// </summary>
public sealed record MetrologyPolarityOption(MetrologyPolarityMode Polarity, string Name);
