using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using TeachingToolWpf.Imaging.Matching;
using TeachingToolWpf.Models;
using VisionAlgorithm.OpenSrc.PatternMatching;
using VisionGeometry;

namespace TeachingToolWpf.Diagnostics;

/// <summary>원본 파일 경로와 픽셀은 기록하지 않는다. UI 소유의 검사별 스냅샷이며 저장은 명시적 내보내기다.</summary>
public sealed record InspectionRunLog(Guid RunId, DateTimeOffset StartedAt, long StartedTimestamp,
    int ImageWidth, int ImageHeight, long SourceGeneration, Guid ModelId,
    RoiGeometry TeachingRegion, RoiGeometry? SearchRegion, TrainingOptions Training, SearchOptions Search,
    ImmutableArray<int> FeaturesPerLevel, int TemplateCount)
{
    public string Event => "paper_inspection";
    public RotationAcceptance? RotationAcceptance
    {
        get; init;
    }
    public int? RotationFailureCount
    {
        get; init;
    }
    public string EntryPoint => "wpf_inspection_command";
    public string Outcome { get; init; } = "running";
    public string Stage { get; init; } = "command";
    public string? ErrorType
    {
        get; init;
    }
    public double? DevicePreparationMilliseconds
    {
        get; init;
    }
    public double? ServiceAwaitMilliseconds
    {
        get; init;
    }
    public double? CommandMilliseconds
    {
        get; init;
    }
    public double? ResultPublishedMilliseconds
    {
        get; init;
    }
    public double? RendererPresentationMilliseconds
    {
        get; init;
    }
    public Guid? PresentationId
    {
        get; init;
    }
    public bool OverlayVisible
    {
        get; init;
    }
    public double? ServiceMilliseconds
    {
        get; init;
    }
    public double? ReadAccumulatedMilliseconds
    {
        get; init;
    }
    public ShapeSearchPhases? WallPhases
    {
        get; init;
    }
    public int? MatchCount
    {
        get; init;
    }
    public int Windows
    {
        get; init;
    }
    public bool Truncated
    {
        get; init;
    }
    public bool MatchLimitExceeded
    {
        get; init;
    }
    public double SearchTimeoutSeconds
    {
        get; init;
    }
    public ImmutableArray<SearchDiagnostics> WorkerDiagnostics { get; init; } = [];
    public ImagePreprocessingDiagnostics? GpuPreprocessing
    {
        get; init;
    }
    public CandidateScoringDiagnostics? GpuCandidates
    {
        get; init;
    }

    [JsonIgnore]
    public double ElapsedMilliseconds => Stopwatch.GetElapsedTime(StartedTimestamp).TotalMilliseconds;

    public InspectionRunLog WithOutput(ShapeSearchOutput output) => this with
    {
        ServiceMilliseconds = output.TotalMilliseconds,
        ReadAccumulatedMilliseconds = output.ReadMilliseconds,
        WallPhases = output.Phases,
        MatchCount = output.Matches.Length,
        Windows = output.Windows,
        Truncated = output.Truncated,
        MatchLimitExceeded = output.MatchLimitExceeded,
        WorkerDiagnostics = output.Diagnostics,
        GpuPreprocessing = output.Preprocessing,
        GpuCandidates = output.CandidateScoring
    };

    public string Describe()
    {
        static string Ms(double? value) => value is { } milliseconds ? $"{milliseconds:F2} ms" : "미측정";
        var phases = WallPhases;
        var text = $"{StartedAt:HH:mm:ss.fff} · {RunId}\n{Outcome} · 마지막 단계 {Stage}\n" +
            $"{ImageWidth}×{ImageHeight} · {Search.ComputeMode} · 학습 {TeachingRegion.Width:F1}×{TeachingRegion.Height:F1} px\n" +
            $"학습 중심 ({TeachingRegion.Center.X:F2}, {TeachingRegion.Center.Y:F2}) · 특징 [{string.Join(", ", FeaturesPerLevel)}]\n" +
            (RotationAcceptance is { } rotation ? $"회전 검사 · 정상 {rotation.MinimumDegrees:G}~{rotation.MaximumDegrees:G}° · 회전 NG {RotationFailureCount?.ToString() ?? "미판정"}개\n" : "회전 허용각 판정 꺼짐\n") +
            $"검사 명령 완료 {Ms(CommandMilliseconds)} (장치 준비 포함)\n" +
            $"결과 게시 {Ms(ResultPublishedMilliseconds)} · GPU 표시 제출/fence {Ms(RendererPresentationMilliseconds)}\n" +
            $"장치 준비 {Ms(DevicePreparationMilliseconds)} · 서비스 await {Ms(ServiceAwaitMilliseconds)}\n" +
            $"서비스 내부 {Ms(ServiceMilliseconds)} · 원본 reader 열기 {Ms(phases?.SourceOpenMilliseconds)}\n";
        if (phases is not null)
        {
            text += $"축소 검색 {Ms(phases.CoarseSearchMilliseconds)} · 원본 정밀 검사 {Ms(phases.OriginalRefinementMilliseconds)}\n" +
                $"원본 창 검색 {Ms(phases.WindowSearchMilliseconds)} · 최종 중복 제거 {Ms(phases.FinalSuppressionMilliseconds)}\n";
        }
        text += $"검출 {MatchCount?.ToString() ?? "미완료"} · 창 {Windows} · 후보 잘림 {Truncated} · 결과 상한 {MatchLimitExceeded}\n" +
            $"[작업 누적 · 서로 겹치므로 전체 시간에 합산 금지]\n읽기/변환 {Ms(ReadAccumulatedMilliseconds)}\n" +
            $"전처리 {Ms(WorkerDiagnostics.Sum(x => x.PreprocessingMilliseconds))}\n" +
            $"후보 검색 {Ms(WorkerDiagnostics.Sum(x => x.CandidateMilliseconds))}" +
            $" (격자 {Ms(WorkerDiagnostics.Sum(x => x.CandidateGridMilliseconds))}, 자세 {Ms(WorkerDiagnostics.Sum(x => x.CandidatePoseMilliseconds))})\n" +
            $"방향/미세 획 검증 {Ms(WorkerDiagnostics.Sum(x => x.VerificationMilliseconds))}\n정밀화/윤곽 점수 {Ms(WorkerDiagnostics.Sum(x => x.RefinementMilliseconds))}\n" +
            $"후보 한도 초과 창 {WorkerDiagnostics.Count(x => x.CandidateBudgetExceeded)} · 검증 한도 초과 창 {WorkerDiagnostics.Count(x => x.VerificationBudgetExceeded)}\n" +
            "표시 시간은 물리 클릭·WPF 패널 렌더 완료·모니터 표시 완료 측정이 아닙니다.";
        if (GpuCandidates is { } gpu)
        {
            text += $"\nCUDA 후보 {gpu.Calls}회 · 전송/대기 포함 {Ms(gpu.TotalMilliseconds)}\n{gpu.AdapterName}";
        }
        return ErrorType is null ? text : text + "\n오류 유형: " + ErrorType;
    }

    public static async Task SaveAsync(IReadOnlyList<InspectionRunLog> runs, string path)
    {
        var json = await Task.Run(() => Serialize(runs)).ConfigureAwait(false);
        await System.IO.File.WriteAllTextAsync(path, json).ConfigureAwait(false);
    }

    public static string Serialize(IEnumerable<InspectionRunLog> runs) => JsonSerializer.Serialize(new
    {
        SchemaVersion = 1,
        Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
        Environment.ProcessorCount,
        ApplicationVersion = typeof(InspectionRunLog).Assembly.GetName().Version?.ToString(),
        TimingContract = "WallPhases are sequential wall times including their reads; WorkerDiagnostics and ReadAccumulatedMilliseconds overlap. RendererPresentation is Present/fence, not physical scan-out or WPF completion.",
        Runs = runs
    }, new JsonSerializerOptions
    {
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter() }
    });
}
