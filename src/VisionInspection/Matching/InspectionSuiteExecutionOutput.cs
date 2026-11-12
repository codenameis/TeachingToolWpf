using System.Collections.Immutable;

namespace TeachingToolWpf.Imaging.Matching;

/// <summary>
/// 검사 스위트 실행 종합 결과.
/// </summary>
public sealed record InspectionSuiteExecutionOutput(
    InspectionSuiteAlgorithmKind Algorithm,
    string SummaryTitle,
    string DetailedReport,
    ImmutableArray<InspectionSuiteItemResult> Items,
    double TotalMilliseconds);
