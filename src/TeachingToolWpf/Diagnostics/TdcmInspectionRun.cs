using PoseMatch.Tdcm;

namespace TeachingToolWpf.Diagnostics;

/// <summary>명령 진입부터 결과 overlay의 Present/fence 완료까지. 물리 scan-out은 포함하지 않는다.</summary>
public sealed record TdcmInspectionRun(Guid RunId, long StartedTimestamp, int Width, int Height, TdcmDevice Device)
{
    public TdcmBackend Backend { get; init; }
    public string? WeightsPath { get; init; }
    public Guid? PresentationId { get; init; }
    public double? ServiceMilliseconds { get; init; }
    public double? PublishedMilliseconds { get; init; }
    public double? CommandMilliseconds { get; init; }
    public double? PresentedMilliseconds { get; init; }
    public int Matches { get; init; }
    public bool Incomplete { get; init; }
    public string Outcome { get; init; } = "Running";
    public string Describe() => $"PoseMatch-TDCM {Backend} {Device} · {Width}×{Height} · {Matches}개\n" +
        $"서비스 {ServiceMilliseconds:F1} ms · 게시 {PublishedMilliseconds:F1} ms · 명령 완료 {CommandMilliseconds:F1} ms\n" +
        (PresentedMilliseconds is { } ms ? $"명령→Present/fence {ms:F1} ms" : "표시 완료 미확인") +
        (Incomplete ? " · 검색 불완전" : "") + $" · {Outcome}\n물리 화면 scan-out 및 입력 메시지 대기 시간은 별도 검증 필요";
}
