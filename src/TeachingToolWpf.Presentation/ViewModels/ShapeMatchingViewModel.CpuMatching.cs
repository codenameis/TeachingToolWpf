using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using TeachingToolWpf.Models;
using TeachingToolWpf.Imaging.Matching;
using VisionAlgorithm.OpenSrc.PatternMatching;
using VisionCanvas.Rendering;
using VisionGeometry;
using CpuMatching.Core;

namespace TeachingToolWpf.ViewModels;

public sealed partial class ShapeMatchingViewModel
{
    private readonly ICpuTemplateMatchingService? _cpuService;
    private readonly Dictionary<PatternMatchingMethod, CpuMatchingModeState> _cpuStates = new()
    {
        [PatternMatchingMethod.CpuLine2D] = new(),
        [PatternMatchingMethod.CpuSubNcc] = new()
    };
    private readonly List<CpuInspectionRun> _cpuLogs = [];
    public IReadOnlyList<CpuInspectionRun> CpuInspectionLogs => _cpuLogs.AsReadOnly();
    public bool IsCpuMethod => _cpuStates.ContainsKey(Method);
    public bool IsCpuSubNcc => Method == PatternMatchingMethod.CpuSubNcc;
    public bool IsCpuEdgeMethod => Method == PatternMatchingMethod.CpuLine2D;
    private CpuMatchingModeState ActiveCpuState => _cpuStates[IsCpuMethod ? Method : PatternMatchingMethod.CpuLine2D];
    private CpuTemplateMethod CpuMethod => Method switch
    {
        PatternMatchingMethod.CpuLine2D => CpuTemplateMethod.Line2D,
        PatternMatchingMethod.CpuSubNcc => CpuTemplateMethod.SubNcc,
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
    public int CpuCoarseFeatures
    {
        get => ActiveCpuState.Parameters.Line2D.CoarseFeatures;
        set => ChangeCpuParameters(ActiveCpuState.Parameters with
        {
            Line2D = ActiveCpuState.Parameters.Line2D with
            {
                CoarseFeatures = value
            }
        });
    }
    public int CpuFineFeatures
    {
        get => ActiveCpuState.Parameters.Line2D.FineFeatures;
        set => ChangeCpuParameters(ActiveCpuState.Parameters with
        {
            Line2D = ActiveCpuState.Parameters.Line2D with
            {
                FineFeatures = value
            }
        });
    }
    public int CpuNccCoarseSamples
    {
        get => ActiveCpuState.Parameters.SubNcc.CoarseSamples;
        set => ChangeCpuParameters(ActiveCpuState.Parameters with { SubNcc = ActiveCpuState.Parameters.SubNcc with { CoarseSamples = value } });
    }
    public int CpuNccMinimumContrast
    {
        get => ActiveCpuState.Parameters.SubNcc.MinimumContrast;
        set => ChangeCpuParameters(ActiveCpuState.Parameters with { SubNcc = ActiveCpuState.Parameters.SubNcc with { MinimumContrast = value } });
    }

    private void ChangeCpuParameters(CpuTemplateParameters parameters, [CallerMemberName] string? property = null)
    {
        if (ActiveCpuState.Parameters == parameters)
        {
            return;
        }
        var wasIdle = IsIdle;
        bool canScheduleLivePreview = wasIdle && ShowFeatures && ShowOverlay && _toolAvailable && !_disposed && _image is not null;
        ActiveCpuState.Parameters = parameters;
        Notify(property);
        Notify(nameof(IsTrainingDirty));
        Notify(nameof(ModelSummary));
        Invalidate(keepLivePreview: canScheduleLivePreview);
        RefreshCommands();
        if (wasIdle)
        {
            ScheduleLivePreview(trainingParametersChanged: true);
        }
    }
    private string CpuSummary => ActiveCpuState.Template is { } model
        ? $"{model.Method} · {model.TeachingRegion.Width:F0}×{model.TeachingRegion.Height:F0} px · 학습 방향 {model.TeachingRegion.Angle:F2}°" +
            (IsTrainingDirty ? " · 학습 설정 변경: 재학습 필요" : "")
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
        var wasIdle = IsIdle;
        var hadTrainingPreview = _result?.Summary.Contains("미리보기", StringComparison.Ordinal) == true || _livePreview is not null;
        bool canScheduleLivePreview = wasIdle && (hadTrainingPreview || !HasModel) && ShowFeatures && ShowOverlay && _toolAvailable && !_disposed && _image is not null;
        ActiveCpuState.Options = options;
        Notify(property);
        Invalidate(keepLivePreview: canScheduleLivePreview);
        RefreshCommands();
        if (wasIdle && (hadTrainingPreview || !HasModel))
        {
            ScheduleLivePreview(trainingParametersChanged: false);
        }
    }

    private async Task TrainCpuAsync()
    {
        var image = _image!;
        var roi = _selection!;
        var live = _live;
        var method = Method;
        var cpuMethod = CpuMethod;
        var state = ActiveCpuState;
        var parameters = state.Parameters;
        var exclusion = _selectionExclusion;
        var id = _selectedRoiId;
        var roiName = _selectedRoiName;
        await RunAsync(async (token, epoch) =>
        {
            Status = $"{cpuMethod} 원본 템플릿 등록 중…";
            var template = await _cpuService!.TrainAsync(image, live, roi, cpuMethod, token, exclusion, parameters);
            if (!Current(epoch, token, method))
            {
                return;
            }
            state.Template = template;
            state.TeachingRoiId = id;
            state.IsMaskDirty = false;
            RegisterOrUpdateTrainedPattern(
                method: method,
                roiId: id,
                roiName: roiName,
                teachingRegion: roi,
                teachingMasks: exclusion?.Regions ?? [],
                summary: $"{template.Method} · {roi.Width:F0}×{roi.Height:F0} px · 표본 {template.Matcher.PreviewPoints.Count}개",
                cpuTemplate: template);
            PresentCpuTrainingPreview(template);
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
        var useCuda = ComputeMode == InspectionComputeMode.GpuPreprocessing;
        var execution = useCuda ? "CPU+CUDA" : "CPU";
        var id = Guid.NewGuid();
        if (_cpuLogs.Count == MaximumInspectionLogs)
        {
            _cpuLogs.RemoveAt(0);
        }
        _cpuLogs.Add(new(id, startedTimestamp, model.Method, image.Key, image.Width, image.Height, model.TeachingRegion, region, options)
        {
            UseCuda = useCuda,
            Parameters = model.Parameters
        });
        await RunAsync(async (token, epoch) =>
        {
            Status = $"{model.Method} {execution} 검색 중…";
            var requestedMs = Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds;
            var output = await _cpuService!.SearchAsync(image, live, model, region, options, token, exclusion, useCuda);
            var resumedMs = Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds;
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
            var rawPoints = model.Matcher.PreviewPoints;
            var markers = result.Matches.Select((match, index) =>
            {
                var rad = match.AngleDegrees * Math.PI / 180;
                var cos = Math.Cos(rad) * match.Scale;
                var sin = Math.Sin(rad) * match.Scale;
                var features = rawPoints.Count == 0
                    ? []
                    : rawPoints.Select(p => new PointD(match.X + (cos * p.X) - (sin * p.Y), match.Y + (sin * p.X) + (cos * p.Y))).ToImmutableArray();
                return new MatchMarker(
                    new RoiGeometry(new(match.X, match.Y), model.TeachingRegion.Width * match.Scale,
                        model.TeachingRegion.Height * match.Scale, match.AngleDegrees).Outline.ToImmutableArray(),
                    new(match.X, match.Y), rad, $"#{index + 1} {match.Score:F3}", color, features);
            }).ToImmutableArray();
            _result = new($"{model.Method} {execution} · {markers.Length}개", color, SearchBoundary(image, region), markers, ShowFeatures)
            {
                BatchLabels = true
            };
            SetResultDetails(_inspectionNotice + "\n좌표: 원본 픽셀 · 각도: 영상 기준 시계 방향",
                result.Matches.Select((match, index) =>
                    $"{index + 1} · X {match.X:F3} / Y {match.Y:F3}\n각도 {match.AngleDegrees:F3}° · 배율 {match.Scale:F4} · 점수 {match.Score:F3}"));
            Status = $"{model.Method} · {markers.Length}개 · 서비스 {output.TotalMilliseconds:F1} ms (읽기 {output.ReadMilliseconds:F1}, 검색 {output.SearchMilliseconds:F1})" +
                $" · 후보 {result.CandidateCount}개 / {result.CandidateBatches}묶음" +
                (result.CandidatesTruncated ? " · 후보 예산 초과: 검색 불완전" : "") +
                (result.MatchLimitExceeded ? " · 결과 상한 초과: 일부 생략" : "");
            UpdateCpuLog(id, log => log with
            {
                Output = output,
                PresentationId = _result.PresentationId,
                Outcome = incomplete ? "Incomplete" : "Completed",
                ServiceRequestedMilliseconds = requestedMs,
                UiResumedMilliseconds = resumedMs,
                ResultPreparedMilliseconds = Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds
            });
            PublishResult();
            RecordSingleRoiPatternResult(
                _selectedRoiId,
                _selectedRoiName,
                region,
                Verdict ?? InspectionVerdict.Review,
                result.Matches.Count,
                result.Matches.Count == 0 ? 0.0 : result.Matches[0].Score,
                result.Matches.Count == 0 ? "검출 없음" : $"X={result.Matches[0].X:F2}, Y={result.Matches[0].Y:F2}, θ={result.Matches[0].AngleDegrees:F2}°",
                output.TotalMilliseconds);
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
