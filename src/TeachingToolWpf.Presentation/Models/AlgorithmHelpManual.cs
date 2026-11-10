namespace TeachingToolWpf.Models;

/// <summary>
/// 각 비전 알고리즘·전처리 도구의 상세 사용 매뉴얼 도메인 모델.
/// UI 상단 '?' 도움말 아이콘 클릭 시 펼쳐지는 수식 원리, 단계별 티칭 방법, 파라미터 튜닝 가이드, 현장 문제 해결 팁을 담는다.
/// </summary>
public sealed record AlgorithmHelpManual(
    string Title,
    string Subtitle,
    string PrincipleAndFormula,
    string StepByStepGuide,
    string ParameterTuningGuide,
    string TroubleshootingTips);
