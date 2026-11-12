namespace TeachingToolWpf.Imaging.Matching;

/// <summary>
/// 개별 검사 스위트 알고리즘이 제공하는 상세 수식·티칭 절차·파라미터·트러블슈팅 도움말 명세.
/// </summary>
public sealed record InspectionAlgorithmHelpSpec(
    string Title,
    string Subtitle,
    string FormulaAndPrinciple,
    string TeachingSteps,
    string ParameterGuide,
    string TroubleshootingTips);
