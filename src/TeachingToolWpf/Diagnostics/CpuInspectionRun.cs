using CpuMatching.Core;
using TeachingToolWpf.Imaging.Matching;
using VisionGeometry;

namespace TeachingToolWpf.Diagnostics;

public sealed record CpuInspectionRun(Guid RunId, long StartedTimestamp, CpuTemplateMethod Method, string ImageId,
    int Width, int Height, RoiGeometry TeachingRegion, RoiGeometry? Region, SearchOptions Options)
{
    public Guid? PresentationId
    {
        get; init;
    }
    public CpuSearchOutput? Output
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

    public string Describe() => $"{Method} CPU · {Width}×{Height} · {Output?.Result.Matches.Count ?? 0}개\n" +
        $"읽기 {Output?.ReadMilliseconds:F1} / 검색 {Output?.SearchMilliseconds:F1} / 서비스 {Output?.TotalMilliseconds:F1} ms\n" +
        $"게시 {PublishedMilliseconds:F1} / 명령 완료 {CommandMilliseconds:F1} ms\n" +
        (PresentedMilliseconds is { } ms ? $"명령→Present/fence {ms:F1} ms" : "표시 완료 미확인") +
        $" · {Outcome}\n물리 scan-out 및 입력 메시지 대기 제외";
}
