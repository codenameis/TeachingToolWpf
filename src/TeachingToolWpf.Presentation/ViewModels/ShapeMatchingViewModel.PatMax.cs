// File: src/TeachingToolWpf.Presentation/ViewModels/ShapeMatchingViewModel.PatMax.cs
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using TeachingToolWpf.Commands;
using TeachingToolWpf.Imaging.Matching;
using TeachingToolWpf.Models;
using VisionAlgorithm.OpenSrc.PatMax;
using VisionCanvas.Imaging.Filters;
using VisionCanvas.Rendering;
using VisionGeometry;

namespace TeachingToolWpf.ViewModels;

public sealed record PatMaxPreprocessingFilterOption(
    PatMaxPreprocessingFilterKind Kind,
    string Name,
    string Description)
{
    public override string ToString() => Name;
}

internal sealed record PatMaxUiSettings
{
    public int CoarseGranularity { get; init; } = PatMaxTrainParams.DefaultCoarseGranularity;
    public int FineGranularity { get; init; } = PatMaxTrainParams.DefaultFineGranularity;
    public float EdgeThreshold { get; init; } = PatMaxTrainParams.DefaultEdgeThreshold;
    public float ElasticityPixels { get; init; } = PatMaxTrainParams.DefaultElasticityPixels;
    public PatMaxPreprocessingFilterKind PreprocessingFilter { get; init; } = PatMaxPreprocessingFilterKind.SobelEdgeResponsePreview;
    public float FilterSigma { get; init; } = PatMaxTrainParams.DefaultFilterSigma;
    public bool ShowInspectionFilterPreview { get; init; } = true;
    public double MinAngleDegrees { get; init; } = -15.0;
    public double MaxAngleDegrees { get; init; } = 15.0;
    public bool EnableScale { get; init; } = true;
    public double ScaleMin { get; init; } = 0.95;
    public double ScaleMax { get; init; } = 1.05;
    public float RuntimeEdgeThreshold { get; init; } = PatMaxSearchParams.DefaultRuntimeEdgeThreshold;
    public float MinSeparationPixels { get; init; } = PatMaxSearchParams.DefaultMinSeparationPixels;
    public int MaxCandidates { get; init; } = PatMaxSearchParams.DefaultMaxCandidates;
    public bool IgnorePolarity { get; init; }

    public double AngleStartDegrees => MinAngleDegrees;
    public double AngleExtentDegrees => Math.Max(0.0, MaxAngleDegrees - MinAngleDegrees);
    public double EffectiveScaleMin => EnableScale ? ScaleMin : 1.0;
    public double EffectiveScaleMax => EnableScale ? ScaleMax : 1.0;

    public void Validate(double acceptThreshold, int maxResults)
    {
        if (FineGranularity is < 1 or > PatMaxTrainParams.MaxAllowedGranularity)
        {
            throw new ArgumentException($"Fine Granularity는 1~{PatMaxTrainParams.MaxAllowedGranularity} 범위여야 합니다.");
        }

        if (CoarseGranularity < FineGranularity || CoarseGranularity > PatMaxTrainParams.MaxAllowedGranularity)
        {
            throw new ArgumentException($"Coarse Granularity는 Fine Granularity({FineGranularity}) 이상, {PatMaxTrainParams.MaxAllowedGranularity} 이하여야 합니다.");
        }

        if (!float.IsFinite(EdgeThreshold) || EdgeThreshold <= 0.0f)
        {
            throw new ArgumentException("학습 에지 임계값은 0보다 커야 합니다.");
        }

        if (!float.IsFinite(ElasticityPixels) || ElasticityPixels is < 0.25f or > 16.0f)
        {
            throw new ArgumentException("인력장 탄성 반경(Elasticity)은 0.25~16 px 범위여야 합니다.");
        }

        if (!Enum.IsDefined(PreprocessingFilter))
        {
            throw new ArgumentException("유효한 PatMax 검사 전처리 필터를 선택하세요.");
        }

        if (!float.IsFinite(FilterSigma) || FilterSigma is < 0.1f or > 16.0f)
        {
            throw new ArgumentException("필터 강도(Sigma)는 0.1~16 범위여야 합니다.");
        }

        if (!double.IsFinite(MinAngleDegrees) || !double.IsFinite(MaxAngleDegrees) || MaxAngleDegrees < MinAngleDegrees || (MaxAngleDegrees - MinAngleDegrees) > 360.0)
        {
            throw new ArgumentException("탐색 각도 범위는 최소 각도 ≤ 최대 각도(폭 최대 360°)여야 합니다.");
        }

        if (EnableScale && (!double.IsFinite(ScaleMin) || !double.IsFinite(ScaleMax) || ScaleMin <= 0.0 || ScaleMax < ScaleMin))
        {
            throw new ArgumentException("배율 범위는 0보다 크고 최소 배율 ≤ 최대 배율이어야 합니다.");
        }

        if (!float.IsFinite(RuntimeEdgeThreshold) || RuntimeEdgeThreshold <= 0.0f)
        {
            throw new ArgumentException("런타임 에지 임계값은 0보다 커야 합니다.");
        }

        if (!float.IsFinite(MinSeparationPixels) || MinSeparationPixels < 1.0f)
        {
            throw new ArgumentException("최소 분리 거리는 1 px 이상이어야 합니다.");
        }

        if (MaxCandidates is < 1 or > 128)
        {
            throw new ArgumentException("PatQuick 최대 후보 수는 1~128 범위여야 합니다.");
        }

        _ = ToTrainParams();
        _ = ToSearchParams(acceptThreshold, maxResults);
    }

    public PatMaxTrainParams ToTrainParams() => PatMaxTrainParams.Create(
        coarseGranularity: CoarseGranularity,
        fineGranularity: FineGranularity,
        edgeThreshold: EdgeThreshold,
        elasticityPixels: ElasticityPixels,
        preprocessingFilter: PreprocessingFilter,
        filterSigma: FilterSigma);

    public PatMaxSearchParams ToSearchParams(double acceptThreshold, int maxResults) => PatMaxSearchParams.Create(
        acceptThreshold: (float)Math.Clamp(acceptThreshold, 0.05, 1.0),
        angleStartRadians: (float)(MinAngleDegrees * Math.PI / 180.0),
        angleExtentRadians: (float)(Math.Max(0.0, MaxAngleDegrees - MinAngleDegrees) * Math.PI / 180.0),
        scaleMin: (float)EffectiveScaleMin,
        scaleMax: (float)EffectiveScaleMax,
        runtimeEdgeThreshold: RuntimeEdgeThreshold,
        minSeparationPixels: MinSeparationPixels,
        ignorePolarity: IgnorePolarity,
        maxCandidates: Math.Clamp(Math.Max(MaxCandidates, maxResults * 2), 1, 128),
        maxResults: Math.Max(1, maxResults));
}

internal sealed class PatMaxModeState
{
    public Guid? TeachingRoiId { get; set; }
    public ImmutableArray<MaskRegion> TeachingMasks { get; set; } = [];
    public bool IsMaskDirty { get; set; }
    public PatMaxTrainedTemplate? Template { get; set; }
    public PatMaxTrainedTemplate? PreviewTemplate { get; set; }
    public FilterPreviewResult? FilterPreview { get; set; }
    public PatMaxUiSettings Settings { get; set; } = new();
    public string LastRunLogText { get; set; } = "PatMax 검사 후 읽기·Coarse 탐색·4-Step WLS 정합 시간이 기록됩니다.";
    public MatchingSearchSettings Search { get; set; } = new()
    {
        Options = new()
        {
            MinimumScore = 0.65,
            MaximumMatches = 16,
            MaximumCandidates = 32
        }
    };
}

public sealed partial class ShapeMatchingViewModel
{
    private readonly IPatMaxMatchingService? _patMaxService;
    private readonly PatMaxModeState _patMaxState = new();
    private RelayCommand? _patMaxAutoGranularityCommand;

    public IReadOnlyList<PatMaxPreprocessingFilterOption> PatMaxFilterOptions { get; } =
    [
        new(
            PatMaxPreprocessingFilterKind.SobelEdgeResponsePreview,
            "가우시안-소벨 에지 응답 필터 (Fine 입도 · 에지 임계값 연동)",
            "실제 PatMax 경계 다이폴 추출에 쓰이는 5-tap 가우시안 평활화 + Sobel 그래디언트 임계값 응답 영상을 ROI에 실시간 표시합니다."),
        new(
            PatMaxPreprocessingFilterKind.GaussianScaleSpace,
            "가우시안 스케일 평활화 필터 (Fine 입도 · Sigma 연동)",
            "학습 및 런타임 정밀 정합 창에 5-tap 가우시안 스케일 평활화를 실제 적용하여 고주파 노이즈를 억제합니다."),
        new(
            PatMaxPreprocessingFilterKind.EdgeContrastEnhance,
            "언샤프 에지 대비 강화 필터 (저대비 경계 부스트)",
            "학습 및 런타임 정밀 정합 창에 가우시안 언샤프 마스킹을 실제 적용하여 흐릿한 경계의 그래디언트 대비를 강화합니다."),
        new(
            PatMaxPreprocessingFilterKind.MedianDenoise,
            "3×3 미디언 노이즈 제거 필터 (임펄스 노이즈 억제)",
            "학습 및 런타임 정밀 정합 창에 경계 보존형 3×3 미디언 필터를 실제 적용하여 점 노이즈를 제거합니다."),
        new(
            PatMaxPreprocessingFilterKind.None,
            "원본 영상 (전처리 필터 없음)",
            "별도 전처리 필터 없이 원본 그레이스케일 영상에서 곧바로 Granularity 피라미드와 에지 다이폴을 추출합니다.")
    ];

    public bool IsPatMaxMethod => Method == PatternMatchingMethod.PatMax;
    public bool IsNonPatMaxMethod => Method != PatternMatchingMethod.PatMax;
    public FilterPreviewResult? FilterPreview => IsPatMaxMethod && ShowOverlay && _patMaxState.Settings.ShowInspectionFilterPreview
        ? _patMaxState.FilterPreview
        : null;

    public RelayCommand PatMaxAutoGranularityCommand => _patMaxAutoGranularityCommand ??= new RelayCommand(
        ApplyPatMaxAutoGranularity,
        () => IsIdle && (_selection is not null || _patMaxState.Template is not null));

    public PatMaxPreprocessingFilterKind PatMaxPreprocessingFilter
    {
        get => _patMaxState.Settings.PreprocessingFilter;
        set
        {
            ChangePatMaxSettings(_patMaxState.Settings with { PreprocessingFilter = value }, isExtractionParam: true);
            Notify(nameof(PatMaxUsesFilterSigma));
            Notify(nameof(PatMaxFilterDescription));
        }
    }

    public float PatMaxFilterSigma
    {
        get => _patMaxState.Settings.FilterSigma;
        set => ChangePatMaxSettings(_patMaxState.Settings with { FilterSigma = value }, isExtractionParam: true);
    }

    public bool PatMaxShowInspectionFilterPreview
    {
        get => _patMaxState.Settings.ShowInspectionFilterPreview;
        set
        {
            if (_patMaxState.Settings.ShowInspectionFilterPreview == value)
            {
                return;
            }

            _patMaxState.Settings = _patMaxState.Settings with { ShowInspectionFilterPreview = value };
            Notify();
            Notify(nameof(FilterPreview));
            if (value && _patMaxState.FilterPreview is null && IsIdle)
            {
                ScheduleLivePreview(trainingParametersChanged: true);
            }
            Changed?.Invoke();
        }
    }

    public bool PatMaxUsesFilterSigma => PatMaxPreprocessingFilter is
        PatMaxPreprocessingFilterKind.GaussianScaleSpace or
        PatMaxPreprocessingFilterKind.EdgeContrastEnhance or
        PatMaxPreprocessingFilterKind.SobelEdgeResponsePreview;

    public string PatMaxFilterDescription => PatMaxFilterOptions
        .FirstOrDefault(o => o.Kind == PatMaxPreprocessingFilter)?.Description ?? "";

    public int PatMaxCoarseGranularity
    {
        get => _patMaxState.Settings.CoarseGranularity;
        set
        {
            var fine = Math.Min(_patMaxState.Settings.FineGranularity, Math.Max(1, value));
            var linkedSigma = MathF.Max(0.5f, fine * 0.65f);
            ChangePatMaxSettings(
                _patMaxState.Settings with { CoarseGranularity = value, FineGranularity = fine, FilterSigma = linkedSigma },
                isExtractionParam: true);
            Notify(nameof(PatMaxFineGranularity));
            Notify(nameof(PatMaxFilterSigma));
        }
    }

    public int PatMaxFineGranularity
    {
        get => _patMaxState.Settings.FineGranularity;
        set
        {
            var minCoarse = Math.Min(PatMaxTrainParams.MaxAllowedGranularity, Math.Max(4, value * 2));
            var coarse = Math.Max(_patMaxState.Settings.CoarseGranularity, minCoarse);
            var linkedSigma = MathF.Max(0.5f, Math.Max(1, value) * 0.65f);
            ChangePatMaxSettings(
                _patMaxState.Settings with { FineGranularity = value, CoarseGranularity = coarse, FilterSigma = linkedSigma },
                isExtractionParam: true);
            Notify(nameof(PatMaxCoarseGranularity));
            Notify(nameof(PatMaxFilterSigma));
        }
    }

    public float PatMaxEdgeThreshold
    {
        get => _patMaxState.Settings.EdgeThreshold;
        set
        {
            var linkedRuntime = MathF.Max(5.0f, MathF.Round(value * 0.8f, 1));
            ChangePatMaxSettings(
                _patMaxState.Settings with { EdgeThreshold = value, RuntimeEdgeThreshold = linkedRuntime },
                isExtractionParam: true);
            Notify(nameof(PatMaxRuntimeEdgeThreshold));
        }
    }

    public float PatMaxElasticityPixels
    {
        get => _patMaxState.Settings.ElasticityPixels;
        set => ChangePatMaxSettings(_patMaxState.Settings with { ElasticityPixels = value }, isExtractionParam: false);
    }

    public double PatMaxMinAngleDegrees
    {
        get => _patMaxState.Settings.MinAngleDegrees;
        set => ChangePatMaxSettings(_patMaxState.Settings with { MinAngleDegrees = value }, isExtractionParam: false);
    }

    public double PatMaxMaxAngleDegrees
    {
        get => _patMaxState.Settings.MaxAngleDegrees;
        set => ChangePatMaxSettings(_patMaxState.Settings with { MaxAngleDegrees = value }, isExtractionParam: false);
    }

    public double PatMaxAngleStartDegrees
    {
        get => _patMaxState.Settings.MinAngleDegrees;
        set => PatMaxMinAngleDegrees = value;
    }

    public double PatMaxAngleExtentDegrees
    {
        get => _patMaxState.Settings.AngleExtentDegrees;
        set => PatMaxMaxAngleDegrees = _patMaxState.Settings.MinAngleDegrees + Math.Max(0.0, value);
    }

    public bool PatMaxEnableScale
    {
        get => _patMaxState.Settings.EnableScale;
        set => ChangePatMaxSettings(_patMaxState.Settings with { EnableScale = value }, isExtractionParam: false);
    }

    public double PatMaxScaleMin
    {
        get => _patMaxState.Settings.ScaleMin;
        set => ChangePatMaxSettings(_patMaxState.Settings with { ScaleMin = value }, isExtractionParam: false);
    }

    public double PatMaxScaleMax
    {
        get => _patMaxState.Settings.ScaleMax;
        set => ChangePatMaxSettings(_patMaxState.Settings with { ScaleMax = value }, isExtractionParam: false);
    }

    public float PatMaxRuntimeEdgeThreshold
    {
        get => _patMaxState.Settings.RuntimeEdgeThreshold;
        set => ChangePatMaxSettings(_patMaxState.Settings with { RuntimeEdgeThreshold = value }, isExtractionParam: false);
    }

    public float PatMaxMinSeparationPixels
    {
        get => _patMaxState.Settings.MinSeparationPixels;
        set => ChangePatMaxSettings(_patMaxState.Settings with { MinSeparationPixels = value }, isExtractionParam: false);
    }

    public int PatMaxMaxCandidates
    {
        get => _patMaxState.Settings.MaxCandidates;
        set => ChangePatMaxSettings(_patMaxState.Settings with { MaxCandidates = value }, isExtractionParam: false);
    }

    public bool PatMaxIgnorePolarity
    {
        get => _patMaxState.Settings.IgnorePolarity;
        set => ChangePatMaxSettings(_patMaxState.Settings with { IgnorePolarity = value }, isExtractionParam: false);
    }

    public string PatMaxDiagnosticsText
    {
        get
        {
            var active = (!IsTrainingDirty ? _patMaxState.Template : null) ?? _patMaxState.PreviewTemplate ?? _patMaxState.Template;
            if (active is null)
            {
                return "ROI를 선택하면 Fine 에지 다이폴(초록), PatQuick Coarse 프로브(하늘), ±Elasticity 인력장 대역(주황)이 실시간으로 표시됩니다.";
            }

            var fineCount = active.PreviewPoints.Length;
            var coarseCount = active.CoarsePreviewPoints.IsDefaultOrEmpty ? 0 : active.CoarsePreviewPoints.Length;
            var stability = active.Model.Stability;
            var warn = stability.HasApertureWarning
                ? "⚠ 단일 방향 에지 편중 (위치/각도 모호성 주의)"
                : "양방향 기하 구속 양호";
            return $"Fine 다이폴 {fineCount}개(초록) · Coarse 프로브 {coarseCount}개(하늘) · 인력장 ±{_patMaxState.Settings.ElasticityPixels:0.#}px(주황)\n" +
                   $"피라미드 {active.Model.LevelCount}단계(Coarse 1/{active.Model.CoarseLevel.ScaleFactor} · Fine 1/{active.Model.FineLevel.ScaleFactor}) · 안정성 κ={stability.ConditionNumber:F2} ({warn})";
        }
    }

    private string PatMaxSummary => _patMaxState.Template is { } tpl
        ? $"PatMax · Fine {tpl.Model.FineLevel.Dipoles.Count}개 / Coarse {tpl.Model.CoarseLevel.Dipoles.Count}개 · 옥타브 {tpl.Model.LevelCount}단계 · κ={tpl.Model.Stability.ConditionNumber:F2}" +
          (tpl.Model.Stability.HasApertureWarning ? " (단일방향 경고)" : "") +
          (IsTrainingDirty ? " · 설정 변경: 재학습 필요" : "")
        : "PatMax 모델 없음 · ROI를 선택해 학습하세요.";

    private void ApplyPatMaxAutoGranularity()
    {
        var roi = _selection ?? _patMaxState.Template?.TeachingRegion;
        if (roi is null)
        {
            return;
        }

        var minDim = Math.Min(roi.Width, roi.Height);
        var fine = minDim switch
        {
            >= 240 => 2,
            _ => 1
        };
        var coarse = minDim switch
        {
            < 36 => 2,
            < 96 => 4,
            < 220 => 6,
            _ => 8
        };
        coarse = Math.Max(fine, coarse);
        ChangePatMaxSettings(
            _patMaxState.Settings with
            {
                FineGranularity = fine,
                CoarseGranularity = coarse
            },
            isExtractionParam: true);
        Notify(nameof(PatMaxFineGranularity));
        Notify(nameof(PatMaxCoarseGranularity));
    }

    private void ChangePatMaxSettings(PatMaxUiSettings updated, bool isExtractionParam, [CallerMemberName] string? property = null)
    {
        if (_patMaxState.Settings == updated)
        {
            return;
        }

        var wasIdle = IsIdle;
        bool canScheduleLivePreview = wasIdle && ShowFeatures && ShowOverlay && _toolAvailable && !_disposed && _image is not null;
        _patMaxState.Settings = updated;
        if (isExtractionParam)
        {
            _patMaxState.PreviewTemplate?.Dispose();
            _patMaxState.PreviewTemplate = null;
        }

        Notify(property);
        Notify(nameof(IsTrainingDirty));
        Notify(nameof(ModelSummary));
        Notify(nameof(PatMaxDiagnosticsText));
        Invalidate(keepLivePreview: canScheduleLivePreview);
        if (_patMaxState.Template is not null && IsTrainingDirty)
        {
            ResetResult("PatMax 학습 파라미터 변경 · 미리보기를 확인한 뒤 ‘선택 ROI 학습’을 다시 누르세요.", clearLivePreview: !canScheduleLivePreview);
        }

        RefreshCommands();
        if (wasIdle)
        {
            ScheduleLivePreview(trainingParametersChanged: isExtractionParam);
        }
    }

    private async Task TrainPatMaxAsync()
    {
        var image = _image!;
        var roi = _selection!;
        var live = _live;
        var method = Method;
        var settings = _patMaxState.Settings;
        var trainParams = settings.ToTrainParams();
        var exclusion = _selectionExclusion;
        var roiId = _selectedRoiId;
        var roiName = _selectedRoiName;

        await RunAsync(async (token, epoch) =>
        {
            Status = "PatMax 다중 스케일 다이폴 및 2D 인력장 빌드 중…";
            var template = await _patMaxService!.TrainAsync(image, live, roi, trainParams, token, exclusion);
            if (!Current(epoch, token, method))
            {
                template.Dispose();
                return;
            }

            _patMaxState.PreviewTemplate?.Dispose();
            _patMaxState.PreviewTemplate = null;
            var oldTemplate = _patMaxState.Template;
            _patMaxState.Template = template;
            _patMaxState.TeachingRoiId = roiId;
            _patMaxState.TeachingMasks = exclusion?.Regions ?? [];
            _patMaxState.IsMaskDirty = false;
            RegisterOrUpdateTrainedPattern(
                method: method,
                roiId: roiId,
                roiName: roiName,
                teachingRegion: roi,
                teachingMasks: _patMaxState.TeachingMasks,
                summary: $"PatMax {roi.Width:F0}×{roi.Height:F0} px · Fine {template.PreviewPoints.Length} / Coarse {template.CoarsePreviewPoints.Length}개",
                patMaxTemplate: template,
                patMaxSettings: settings);
            if (oldTemplate is not null && !ReferenceEquals(oldTemplate, template) &&
                !RegisteredPatterns.Any(p => ReferenceEquals(p.PatMaxTemplate, oldTemplate)))
            {
                oldTemplate.Dispose();
            }
            PresentPatMaxTrainingPreview(template);
            Status = $"PatMax 읽기 {template.ReadMilliseconds:F1} ms · 학습/인력장 {template.TrainMilliseconds:F1} ms";
            Notify(nameof(ModelSummary));
            Notify(nameof(PatMaxDiagnosticsText));
            Notify(nameof(HasModel));
            Notify(nameof(IsTrainingDirty));
            PublishResult();
        });
    }

    private FilterPreviewResult? BuildPatMaxFilterPreview(PatMaxTrainedTemplate template)
    {
        if (template.TrainParams.PreprocessingFilter == PatMaxPreprocessingFilterKind.None ||
            template.FilteredPreviewGray8 is not { Length: > 0 } bytes ||
            template.PreviewWidth <= 0 ||
            template.PreviewHeight <= 0)
        {
            return null;
        }

        var id = _selectedRoiId ?? _patMaxState.TeachingRoiId ?? Guid.Empty;
        var filteredRoi = new FilteredRoi(
            id,
            template.TeachingRegion,
            template.PreviewWidth,
            template.PreviewHeight,
            bytes,
            template.PreviewMask);
        return new FilterPreviewResult([filteredRoi], template.TrainMilliseconds);
    }

    private void PresentPatMaxTrainingPreview(PatMaxTrainedTemplate template)
    {
        ResetResultRows();
        _livePreview = null;
        _patMaxState.FilterPreview = BuildPatMaxFilterPreview(template);
        Verdict = template.Model.Stability.HasApertureWarning ? InspectionVerdict.Warning : null;
        var s = _patMaxState.Settings;
        var msg = template.Model.Stability.HasApertureWarning
            ? $"학습 완료 (경고: 단일 방향 에지 위주 조건수 κ={template.Model.Stability.ConditionNumber:F1}) · Fine 다이폴 {template.PreviewPoints.Length}개 / Coarse 프로브 {template.CoarsePreviewPoints.Length}개"
            : $"PatMax 학습 완료 · Fine 다이폴 {template.PreviewPoints.Length}개(초록) · Coarse 프로브 {template.CoarsePreviewPoints.Length}개(하늘) · 인력장 ±{s.ElasticityPixels:F1}px(주황)";
        Status = msg;
        Results = msg;
        _result = new(
            "PatMax · 다이폴 · Coarse 프로브 · 인력장 대역 미리보기",
            new(0.58f, 0.64f, 0.72f, 1),
            [],
            InspectionResultPresentation.PatMaxTrainingPreviewMarkers(
                template,
                s.MinAngleDegrees,
                s.MaxAngleDegrees,
                s.EffectiveScaleMin,
                s.EffectiveScaleMax,
                s.ElasticityPixels),
            ShowFeatures: true);
        Notify(nameof(FilterPreview));
        PublishResult();
    }

    private async Task SearchPatMaxAsync()
    {
        var image = _image!;
        var method = Method;
        var template = _patMaxState.Template!;
        var region = UseSelection ? _searchSelection : null;
        var exclusion = UseSelection ? _selectionExclusion : null;
        var live = _live;
        var searchParams = _patMaxState.Settings.ToSearchParams(MinimumScore, MaximumMatches);

        await RunAsync(async (token, epoch) =>
        {
            Status = "PatQuick Coarse Search + PatMax 4-Step WLS 정밀 정합 중…";
            var output = await _patMaxService!.SearchAsync(image, live, template, region, searchParams, token, exclusion);
            if (!Current(epoch, token, method) || !ReferenceEquals(template, _patMaxState.Template))
            {
                return;
            }

            Verdict = output.MatchLimitExceeded
                ? InspectionVerdict.Warning
                : output.Matches.IsEmpty
                    ? InspectionVerdict.Review
                    : InspectionVerdict.Pass;
            var color = InspectionResultPresentation.Color(Verdict.Value);
            var rawPoints = template.PreviewPoints;
            var markers = output.Matches.Select((match, idx) =>
            {
                var rad = match.AngleDegrees * Math.PI / 180.0;
                var cos = Math.Cos(rad) * match.Scale;
                var sin = Math.Sin(rad) * match.Scale;
                var features = rawPoints.IsDefaultOrEmpty
                    ? []
                    : rawPoints.Select(p => new PointD(match.X + (cos * p.X) - (sin * p.Y), match.Y + (sin * p.X) + (cos * p.Y))).ToImmutableArray();
                return new MatchMarker(
                    new RoiGeometry(new(match.X, match.Y), template.TeachingRegion.Width * match.Scale,
                        template.TeachingRegion.Height * match.Scale * match.AspectRatio, match.AngleDegrees).Outline.ToImmutableArray(),
                    new(match.X, match.Y),
                    rad,
                    $"#{idx + 1} {match.Score:F3} (Cov {match.Coverage:P0})",
                    color,
                    features);
            }).ToImmutableArray();

            _result = new($"PatMax · {markers.Length}개 검출", color, SearchBoundary(image, region), markers, ShowFeatures)
            {
                BatchLabels = true
            };

            SetResultDetails(
                DetectionNotice(false, output.MatchLimitExceeded, output.Matches.Length) + "\nPatMax 서브픽셀 정합 (X, Y, 각도, 배율, Coverage, Clutter)",
                output.Matches.Select((m, idx) =>
                    $"{idx + 1} · X {m.X:F3} / Y {m.Y:F3} · 각도 {m.AngleDegrees:F3}° · 배율 {m.Scale:F4}\n점수 {m.Score:F3} (Coverage {m.Coverage:P1} · Clutter {m.Clutter:P1} · 다이폴 {m.MatchedDipoles}개 · {m.Iterations}-Step)"));

            var logSummary = $"PatMax · 검출 {markers.Length}개 · 총 {output.TotalMilliseconds:F1} ms (영상 읽기 {output.ReadMilliseconds:F1} ms, PatQuick+WLS 정합 {output.SearchMilliseconds:F1} ms)";
            _patMaxState.LastRunLogText =
                $"{DateTimeOffset.Now:HH:mm:ss} · {logSummary}\n" +
                $"Fine 다이폴 {template.PreviewPoints.Length}개 · Coarse 프로브 {template.CoarsePreviewPoints.Length}개 · 후보 상한 {_patMaxState.Settings.MaxCandidates}개 · Elasticity ±{_patMaxState.Settings.ElasticityPixels:F1}px";
            Notify(nameof(InspectionLogText));
            Status = logSummary;
            PublishResult();
            RecordSingleRoiPatternResult(
                _selectedRoiId,
                _selectedRoiName,
                region,
                Verdict ?? InspectionVerdict.Review,
                output.Matches.Length,
                output.Matches.IsEmpty ? 0.0 : output.Matches[0].Score,
                output.Matches.IsEmpty ? "검출 없음" : $"X={output.Matches[0].X:F2}, Y={output.Matches[0].Y:F2}, θ={output.Matches[0].AngleDegrees:F2}°",
                output.TotalMilliseconds);
        });
    }
}
