using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using PoseMatch.Tdcm;
using TeachingToolWpf.Diagnostics;
using TeachingToolWpf.Imaging.Matching;
using TeachingToolWpf.Models;
using VisionCanvas.Rendering;
using VisionGeometry;

namespace TeachingToolWpf.ViewModels;

public sealed partial class ShapeMatchingViewModel
{
    private readonly ITdcmMatchingService? _tdcmService;
    private TdcmTemplate? _tdcmTemplate;
    private Guid? _tdcmTeachingId;
    private bool _tdcmMaskDirty;
    private MatchingSearchSettings _tdcmSearch = new() { Options = new() { MinimumScore = .5, MaximumMatches = 256, MaximumCandidates = 4096 } };
    private TdcmSearchOptions _tdcmOptions = new() { Backend = TdcmBackend.TorchSharp };
    public IReadOnlyList<TdcmBackend> TdcmBackends { get; } = Enum.GetValues<TdcmBackend>();
    public TdcmBackend TdcmBackend
    {
        get => _tdcmOptions.Backend; set => ChangeTdcm(_tdcmOptions with { Backend = value });
    }
    public string? TdcmWeightsPath
    {
        get => _tdcmOptions.WeightsPath; set => ChangeTdcm(_tdcmOptions with { WeightsPath = string.IsNullOrWhiteSpace(value) ? null : value.Trim() });
    }
    public bool IsTdcmMethod => Method == PatternMatchingMethod.PoseMatchTdcm;
    public IReadOnlyList<TdcmDevice> TdcmDevices { get; } = Enum.GetValues<TdcmDevice>();
    public TdcmDevice TdcmDevice
    {
        get => _tdcmOptions.Device; set => ChangeTdcm(_tdcmOptions with { Device = value });
    }
    public bool SupportsOverlapRatio => !IsTdcmMethod && !IsCpuMethod;
    public bool TdcmRefinePose
    {
        get => _tdcmOptions.Matching.RefinePose; set => ChangeTdcm(_tdcmOptions with { Matching = _tdcmOptions.Matching with { RefinePose = value } });
    }
    public int TdcmWindowSize
    {
        get => _tdcmOptions.WindowSize; set => ChangeTdcm(_tdcmOptions with { WindowSize = value });
    }
    public int TdcmThreads
    {
        get => _tdcmOptions.Threads; set => ChangeTdcm(_tdcmOptions with { Threads = value });
    }
    public int TdcmMaximumWindows
    {
        get => _tdcmOptions.MaximumWindows; set => ChangeTdcm(_tdcmOptions with { MaximumWindows = value });
    }
    public int TdcmSuppressionRadius
    {
        get => _tdcmOptions.Matching.SuppressionRadius; set => ChangeTdcm(_tdcmOptions with { Matching = _tdcmOptions.Matching with { SuppressionRadius = value } });
    }
    public int TdcmCenterRadius
    {
        get => _tdcmOptions.Matching.CenterRadius; set => ChangeTdcm(_tdcmOptions with { Matching = _tdcmOptions.Matching with { CenterRadius = value } });
    }
    private string TdcmSummary => _tdcmTemplate is { } model ? $"PoseMatch-TDCM · 학습 {model.TeachingRegion.Width:F0}×{model.TeachingRegion.Height:F0} → 36×36 · 검색 축소 1/{model.Reduction}" : "PoseMatch-TDCM 모델 없음 · 사각형 ROI를 선택해 학습하세요.";
    private TdcmSearchOptions CreateTdcmOptions() => _tdcmOptions with
    {
        Matching = _tdcmOptions.Matching with
        {
            MinimumScore = (float)MinimumScore,
            MaximumMatches = MaximumMatches,
            MaximumCandidates = MaximumCandidates
        }
    };
    private void ChangeTdcm(TdcmSearchOptions options, [CallerMemberName] string? property = null)
    {
        if (_tdcmOptions == options)
        {
            return;
        }

        _tdcmOptions = options;
        Notify(property);
        Invalidate();
        RefreshCommands();
    }

    private async Task TrainTdcmAsync()
    {
        var image = _image!;
        var roi = _selection!;
        var live = _live;
        var exclusion = _selectionExclusion;
        var id = _selectedRoiId;
        await RunAsync(async (token, epoch) =>
        {
            Status = "PoseMatch-TDCM 템플릿 등록 중… 신경망 가중치는 변경하지 않습니다.";
            var model = await _tdcmService!.TrainAsync(image, live, roi, token, exclusion);
            if (!Current(epoch, token, PatternMatchingMethod.PoseMatchTdcm))
            {
                return;
            }

            _tdcmTemplate = model;
            _tdcmTeachingId = id;
            _tdcmMaskDirty = false;
            PresentActivity("PoseMatch-TDCM 학습 완료 · 검사 실행을 누르세요.");
            Status = TdcmSummary;
            Notify(nameof(ModelSummary));
            Notify(nameof(HasModel));
            Notify(nameof(IsTrainingDirty));
            PublishResult();
        });
    }

    private async Task SearchTdcmAsync(long startedTimestamp)
    {
        var image = _image!;
        var model = _tdcmTemplate!;
        var live = _live;
        var region = UseSelection ? _searchSelection : null;
        var exclusion = UseSelection ? _selectionExclusion : null;
        var options = CreateTdcmOptions();
        if (_tdcmLogs.Count == MaximumInspectionLogs)
        {
            _tdcmLogs.RemoveAt(0);
        }

        var runId = Guid.NewGuid();
        _tdcmLogs.Add(new(runId, startedTimestamp, image.Width, image.Height, options.Device)
        {
            Backend = options.Backend,
            WeightsPath = options.WeightsPath
        });
        await RunAsync(async (token, epoch) =>
        {
            Status = $"PoseMatch-TDCM {options.Backend} {options.Device} 추론 중…";
            var output = await _tdcmService!.SearchAsync(image, live, model, region, options, token, exclusion);
            if (!Current(epoch, token, PatternMatchingMethod.PoseMatchTdcm) || !ReferenceEquals(model, _tdcmTemplate))
            {
                return;
            }

            var result = output.Result;
            _inspectionNotice = DetectionNotice(result.CandidatesTruncated, result.MatchLimitExceeded, result.Matches.Count, tdcm: true);
            Verdict = result.CandidatesTruncated || result.MatchLimitExceeded ? InspectionVerdict.Warning : InspectionVerdict.Review;
            var color = InspectionResultPresentation.Color(Verdict.Value);
            var markers = result.Matches.Select((match, index) => new MatchMarker(
                new RoiGeometry(new(match.X, match.Y), match.Width, match.Height, match.AngleDegrees).Outline.ToImmutableArray(),
                new(match.X, match.Y), match.AngleDegrees * Math.PI / 180, $"#{index + 1} 신뢰도 {match.Score:F3}", color, [])).ToImmutableArray();
            _result = new($"PoseMatch-TDCM · {markers.Length}개", color, SearchBoundary(image, region), markers, false)
            {
                BatchLabels = true
            };
            UpdateTdcmLog(runId, log => log with
            {
                PresentationId = _result.PresentationId,
                ServiceMilliseconds = output.TotalMilliseconds,
                Matches = markers.Length,
                Incomplete = result.CandidatesTruncated || result.MatchLimitExceeded,
                Outcome = result.CandidatesTruncated || result.MatchLimitExceeded ? "Incomplete" : "Completed"
            });
            SetResultDetails(_inspectionNotice, result.Matches.Select((match, i) =>
                $"{i + 1} · X {match.X:F2} / Y {match.Y:F2} · 각도 {match.AngleDegrees:F2}°\n폭 {match.Width:F2} / 높이 {match.Height:F2} · 신뢰도 {match.Score:F3}"));
            Status = $"PoseMatch-TDCM {options.Backend} {options.Device} 서비스 {output.TotalMilliseconds:F1} ms · 읽기 {output.ReadMilliseconds:F1} · 세션 준비 {output.InitializationMilliseconds:F1} · 추론/후처리 {output.InferenceMilliseconds:F1} ms · 창 {output.Windows}" +
                (result.CandidatesTruncated ? " · 검색 불완전" : "") + (result.MatchLimitExceeded ? " · 결과 상한 초과" : "");
            PublishResult();
            UpdateTdcmLog(runId, log => log with { PublishedMilliseconds = Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds });
        });
        UpdateTdcmLog(runId, log => log with
        {
            CommandMilliseconds = Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds,
            Incomplete = log.Incomplete || log.PresentationId is null,
            Outcome = log.PresentationId is null ? "FailedOrCancelled: " + Status : log.Outcome
        });
    }
}
