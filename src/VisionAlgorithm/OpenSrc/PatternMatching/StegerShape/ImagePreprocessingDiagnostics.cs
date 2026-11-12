namespace VisionAlgorithm.OpenSrc.PatternMatching;

/// <summary>
/// 완료한 전처리의 누적 실측. TotalMilliseconds는 전송·대기·읽기 반환·CPU 정규화를 포함한 wall time이며
/// GPU timestamp 또는 전체 매칭 시간이 아니다.
/// </summary>
public sealed record ImagePreprocessingDiagnostics(
    string Backend, string AdapterName, long Calls, long ProcessedPixels, double TotalMilliseconds);
