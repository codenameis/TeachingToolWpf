using System.Collections.Immutable;

namespace VisionAlgorithm.OpenSrc.PatternMatching;

/// <summary>후보 점수 계산의 전송·GPU 대기·반환을 포함한 누적 wall time. 전체 검사 시간과 구별한다.</summary>
public sealed record CandidateScoringDiagnostics(string Backend, string AdapterName, long Calls,
    long ProcessedPixels, long TemplateCount, double TotalMilliseconds);
