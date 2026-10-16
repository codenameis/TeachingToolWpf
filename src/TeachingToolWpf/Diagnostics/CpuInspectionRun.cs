using CpuMatching.Core;
using TeachingToolWpf.Imaging.Matching;
using VisionGeometry;

namespace TeachingToolWpf.Diagnostics;

public sealed record CpuInspectionRun(Guid RunId, long StartedTimestamp, CpuTemplateMethod Method, string ImageId,
    int Width, int Height, RoiGeometry TeachingRegion, RoiGeometry? Region, SearchOptions Options)
{
    public CpuTemplateParameters Parameters { get; init; } = new();
    public bool UseCuda
    {
        get; init;
    }
    public Guid? PresentationId
    {
        get; init;
    }
    public CpuSearchOutput? Output
    {
        get; init;
    }
    public double? ServiceRequestedMilliseconds
    {
        get; init;
    }
    public double? UiResumedMilliseconds
    {
        get; init;
    }
    public double? ResultPreparedMilliseconds
    {
        get; init;
    }
    public double? PublishedMilliseconds
    {
        get; init;
    }
    public double? CommandMilliseconds
    {
        get; init;
    }
    public double? PresentedMilliseconds
    {
        get; init;
    }
    public string Outcome { get; init; } = "Running";

    public string Describe() => $"{Method} {(UseCuda ? "CPU+CUDA" : "CPU")} · {Width}×{Height} · {Output?.Result.Matches.Count ?? 0}개\n" +
        (Region is null ? $"검색: 전체 {Width}×{Height} 원본 픽셀\n" : $"검색: 선택 영역 {Region.Width:F0}×{Region.Height:F0} 원본 픽셀\n") +
        $"읽기 {Output?.ReadMilliseconds:F1} / 검색 {Output?.SearchMilliseconds:F1} / 서비스 {Output?.TotalMilliseconds:F1} ms\n" +
        (Output?.Result.Timings is { } stages ? $"검색 내부: 준비 {stages.Preparation:F1} / 후보 {stages.CoarseSearch:F1} / 보정 {stages.Refinement:F1} / 원본 검증 {stages.Verification:F1} ms\n" : "") +
        $"서비스 요청 {ServiceRequestedMilliseconds:F1} / UI 복귀 {UiResumedMilliseconds:F1} / 결과 준비 {ResultPreparedMilliseconds:F1} ms\n" +
        (Output?.Acceleration is { } gpu ? $"{gpu.Device} · 초기화 {gpu.InitializationMilliseconds:F1} / 전송 {gpu.UploadMilliseconds:F1} / GPU 계산·대기 {gpu.ComputeMilliseconds:F1} / 수신 {gpu.DownloadMilliseconds:F1} ms\n" : "") +
        $"게시 {PublishedMilliseconds:F1} / 명령 완료 {CommandMilliseconds:F1} ms\n" +
        (PresentedMilliseconds is { } ms ? $"명령→Present/fence {ms:F1} ms" : "표시 완료 미확인") +
        $" · {Outcome}\n물리 scan-out 및 입력 메시지 대기 제외";
}
