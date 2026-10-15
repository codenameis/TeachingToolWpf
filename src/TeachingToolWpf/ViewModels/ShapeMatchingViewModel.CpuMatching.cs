using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using TeachingToolWpf.Diagnostics;
using TeachingToolWpf.Imaging.Matching;
using TeachingToolWpf.Models;
using VisionCanvas.Rendering;
using VisionGeometry;
using CpuSearchOptions = CpuMatching.Core.SearchOptions;

namespace TeachingToolWpf.ViewModels;

public sealed partial class ShapeMatchingViewModel
{
    private readonly ICpuTemplateMatchingService? _cpuService;
    private readonly Dictionary<PatternMatchingMethod, CpuMatchingModeState> _cpuStates = new()
    {
        [PatternMatchingMethod.CpuLine2D] = new(),
        [PatternMatchingMethod.CpuSubNcc] = new(),
        [PatternMatchingMethod.CpuDirectionalChamfer] = new()
    };
    private readonly List<CpuInspectionRun> _cpuLogs = [];
    public IReadOnlyList<CpuInspectionRun> CpuInspectionLogs => _cpuLogs.AsReadOnly();
    public bool IsCpuMethod => _cpuStates.ContainsKey(Method);
    private CpuMatchingModeState ActiveCpuState => _cpuStates[IsCpuMethod ? Method : PatternMatchingMethod.CpuLine2D];
    private CpuTemplateMethod CpuMethod => Method switch
    {
        PatternMatchingMethod.CpuLine2D => CpuTemplateMethod.Line2D,
        PatternMatchingMethod.CpuSubNcc => CpuTemplateMethod.SubNcc,
        PatternMatchingMethod.CpuDirectionalChamfer => CpuTemplateMethod.DirectionalChamfer,
        _ => throw new InvalidOperationException("CPU 방식을 선택하세요.")
    };
    public double CpuMinimumAngle
    {
        get => ActiveCpuState.Options.MinimumAngle;
        set => ChangeCpuOptions(ActiveCpuState.Options with { MinimumAngle = value });
    }
    public double CpuMaximumAngle
    {
        get => ActiveCpuState.Options.MaximumAngle;
        set => ChangeCpuOptions(ActiveCpuState.Options with { MaximumAngle = value });
    }
    public double CpuAngleStep
    {
        get => ActiveCpuState.Options.AngleStep;
        set => ChangeCpuOptions(ActiveCpuState.Options with { AngleStep = value });
    }
    public double CpuMinimumScale
    {
        get => ActiveCpuState.Options.MinimumScale;
        set => ChangeCpuOptions(ActiveCpuState.Options with { MinimumScale = value });
    }
    public double CpuMaximumScale
    {
        get => ActiveCpuState.Options.MaximumScale;
        set => ChangeCpuOptions(ActiveCpuState.Options with { MaximumScale = value });
    }
    public double CpuScaleStep
    {
        get => ActiveCpuState.Options.ScaleStep;
        set => ChangeCpuOptions(ActiveCpuState.Options with { ScaleStep = value });
    }
    public int CpuPyramidLevels
    {
        get => ActiveCpuState.Options.PyramidLevels;
        set => ChangeCpuOptions(ActiveCpuState.Options with { PyramidLevels = value });
    }
    public double CpuSuppressionRadius
    {
        get => ActiveCpuState.Options.SuppressionRadius;
        set => ChangeCpuOptions(ActiveCpuState.Options with { SuppressionRadius = value });
    }
    private string CpuSummary => ActiveCpuState.Template is { } model
        ? $"{model.Method} CPU · {model.TeachingRegion.Width:F0}×{model.TeachingRegion.Height:F0} px · 학습 방향 {model.TeachingRegion.Angle:F2}°" +
            (IsTrainingDirty ? " · 마스크 변경: 재학습 필요" : "")
        : "CPU 템플릿 없음 · 마스크 없는 사각형 ROI를 선택해 학습하세요.";
    private string CpuLogText => _cpuLogs.LastOrDefault(log => log.Method == CpuMethod)?.Describe() ?? "CPU 검사 후 읽기·검색·게시·표시 시간이 기록됩니다.";

    private CpuSearchOptions CreateCpuOptions() => ActiveCpuState.Options with
    {
        MinimumScore = MinimumScore,
        MaximumMatches = MaximumMatches,
        CandidateLimit = MaximumCandidates
    };

    private void ChangeCpuOptions(CpuSearchOptions options, [CallerMemberName] string? property = null)
    {
        if (ActiveCpuState.Options == options)
        {
            return;
        }
        ActiveCpuState.Options = options;
        Notify(property);
        Invalidate();
        RefreshCommands();
    }

    private async Task TrainCpuAsync()
    {
        var image = _image!;
        var roi = _selection!;
        var live = _live;
        var method = Method;
        var cpuMethod = CpuMethod;
        var state = ActiveCpuState;
        var exclusion = _selectionExclusion;
        var id = _selectedRoiId;
        await RunAsync(async (token, epoch) =>
        {
            Status = $"{cpuMethod} 원본 템플릿 등록 중…";
            var template = await _cpuService!.TrainAsync(image, live, roi, cpuMethod, token, exclusion);
            if (!Current(epoch, token, method))
            {
                return;
            }
            state.Template = template;
            state.TeachingRoiId = id;
            state.IsMaskDirty = false;
            PresentActivity("CPU 템플릿 등록 완료 · 검색 범위와 파라미터를 설정하고 검사하세요.");
            Status = CpuSummary;
            Notify(nameof(ModelSummary));
            Notify(nameof(HasModel));
            Notify(nameof(IsTrainingDirty));
            PublishResult();
        });
    }

    private async Task SearchCpuAsync(long startedTimestamp)
    {
        var image = _image!;
        var state = ActiveCpuState;
        var model = state.Template!;
        var method = Method;
        var region = UseSelection ? _searchSelection : null;
        var exclusion = UseSelection ? _selectionExclusion : null;
        var live = _live;
        var options = CreateCpuOptions();
        var id = Guid.NewGuid();
        if (_cpuLogs.Count == MaximumInspectionLogs)
        {
            _cpuLogs.RemoveAt(0);
        }
        _cpuLogs.Add(new(id, startedTimestamp, model.Method, image.Key, image.Width, image.Height, model.TeachingRegion, region, options));
        await RunAsync(async (token, epoch) =>
        {
            Status = $"{model.Method} CPU 검색 중…";
            var output = await _cpuService!.SearchAsync(image, live, model, region, options, token, exclusion);
            if (!Current(epoch, token, method) || !ReferenceEquals(model, state.Template))
            {
                UpdateCpuLog(id, log => log with { Outcome = "Discarded" });
                return;
            }
            var result = output.Result;
            var incomplete = result.CandidatesTruncated || result.MatchLimitExceeded;
            _inspectionNotice = DetectionNotice(result.CandidatesTruncated, result.MatchLimitExceeded, result.Matches.Count);
            Verdict = incomplete ? InspectionVerdict.Warning : InspectionVerdict.Review;
            var color = InspectionResultPresentation.Color(Verdict.Value);
            var markers = result.Matches.Select((match, index) => new MatchMarker(
                new RoiGeometry(new(match.X, match.Y), model.TeachingRegion.Width * match.Scale,
                    model.TeachingRegion.Height * match.Scale, match.AngleDegrees).Outline.ToImmutableArray(),
                new(match.X, match.Y), match.AngleDegrees * Math.PI / 180, $"#{index + 1} {match.Score:F3}", color, [])).ToImmutableArray();
            _result = new($"{model.Method} CPU · {markers.Length}개", color, SearchBoundary(image, region), markers, false)
            {
                BatchLabels = true
            };
            SetResultDetails(_inspectionNotice + "\n좌표: 원본 픽셀 · 각도: 영상 기준 시계 방향",
                result.Matches.Select((match, index) =>
                    $"{index + 1} · X {match.X:F3} / Y {match.Y:F3}\n각도 {match.AngleDegrees:F3}° · 배율 {match.Scale:F4} · 점수 {match.Score:F3}"));
            Status = $"{model.Method} · {markers.Length}개 · 서비스 {output.TotalMilliseconds:F1} ms (읽기 {output.ReadMilliseconds:F1}, 검색 {output.SearchMilliseconds:F1})" +
                (result.CandidatesTruncated ? " · 후보 예산 초과: 검색 불완전" : "") +
                (result.MatchLimitExceeded ? " · 결과 상한 초과: 일부 생략" : "");
            UpdateCpuLog(id, log => log with { Output = output, PresentationId = _result.PresentationId, Outcome = incomplete ? "Incomplete" : "Completed" });
            PublishResult();
            UpdateCpuLog(id, log => log with { PublishedMilliseconds = Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds });
        });
        UpdateCpuLog(id, log => log with
        {
            CommandMilliseconds = Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds,
            Outcome = log.Outcome == "Running" ? "FailedOrCancelled: " + Status : log.Outcome
        });
    }

    private void UpdateCpuLog(Guid id, Func<CpuInspectionRun, CpuInspectionRun> update)
    {
        var index = _cpuLogs.FindIndex(log => log.RunId == id);
        if (index >= 0)
        {
            _cpuLogs[index] = update(_cpuLogs[index]);
            Notify(nameof(InspectionLogText));
        }
    }
}
