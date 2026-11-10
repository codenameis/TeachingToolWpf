using System.Collections.Immutable;
using System.Diagnostics;
using TeachingToolWpf.Commands;
using TeachingToolWpf.Imaging.Matching;
using TeachingToolWpf.Infrastructure;
using TeachingToolWpf.Models;
using VisionAlgorithm.OpenSrc.PatternMatching;
using VisionCanvas.Core.Editing;
using VisionCanvas.Rendering;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.ViewModels;

/// <summary>세션의 불변 모델과 일회 검사 결과. ROI 문서/편집 이력은 변경하지 않는다.</summary>
public sealed partial class ShapeMatchingViewModel : ObservableObject, IVisionAlgorithmPreview
{
    private readonly IShapeMatchingService _service;
    private ImageReference? _image;
    private DocumentState? _state;
    private object? _lastRois;
    private object? _lastSelection;
    private RoiTransformPreview? _lastTransformPreview;
    private RoiGeometry? _selection;
    private RoiGeometry? _searchSelection;
    private RoiExclusion? _selectionExclusion;
    private Guid? _selectedRoiId;
    private string? _selectedRoiName;
    private int _targetRoiCount;
    private long _generation;
    private bool _live;
    private bool _canExecute;
    private bool _inputAvailable;
    private bool _toolAvailable = true;
    private bool _disposed;
    private CancellationTokenSource? _cancellation;
    private long _epoch;
    private string _status = "ROI 하나를 선택해 모델을 학습하세요. 모델은 이 세션에서 이미지 교체 후에도 유지됩니다.";
    private bool _training;
    private bool _showFeatures;
    private bool _showOverlay = true;
    private ResultOverlay? _result;
    private ResultOverlay? _livePreview;
    private ShapeModel? _previewShapeModel;
    private CancellationTokenSource? _previewCancellation;
    private LivePreviewRequest? _pendingPreview;
    private Task? _previewPump;
    private long _previewEpoch;
    private long _previewGeneration;
    private long _lastPublishedPreviewEpoch;

    private sealed record LivePreviewRequest(
        ImageReference Image,
        bool Live,
        RoiGeometry Roi,
        PatternMatchingMethod Method,
        RoiExclusion? Exclusion,
        SynchronizationContext? UiContext,
        long Epoch,
        long Generation);

    public event Action<BoundsD>? LocateRequested;
    public event Action<Guid>? SearchRangeRequested;
    public event Action? PatternMaskEditorRequested;
    public event Action<RoiAlgorithmAssignment>? RoiAssignmentRecorded;
    public event Action<StructuredInspectionLogEntry>? StructuredLogRecorded;

    public event Action? Changed;
    public AsyncCommand Train { get; }
    public AsyncCommand Search { get; }
    public RelayCommand Cancel { get; }
    public RelayCommand Clear { get; }
    public RelayCommand OpenPatternMaskEditor { get; }
    public bool IsIdle => !_disposed && _cancellation is null;
    public bool IsBusy => _cancellation is not null;
    public ShapeModel? Model => ActiveShapeState.Model;
    public ImmutableArray<MatchMarker> Markers => _result?.Matches ?? [];
    public ResultOverlay? Overlay => ShowOverlay ? (_result ?? _livePreview) : null;
    public InspectionVerdict? Verdict { get; private set; }
    public string VerdictText => Verdict is { } verdict ? InspectionResultPresentation.Name(verdict) : IsIdle ? "검사 대기" : "처리 중";
    public string VerdictColor => Verdict is { } verdict ? InspectionResultPresentation.Hex(verdict) : "#94A3B8";
    public RelayCommand Locate { get; }
    public string ModelSummary => IsPatMaxMethod ? PatMaxSummary : IsCpuMethod ? CpuSummary : !IsShapeMethod ? TemplateSummary : Model is not { } model ? "학습된 모델 없음" :
        $"특징 {model.Levels[0].Features.Length}개 · {model.Levels.Length}단계(자동) · 템플릿 {model.TemplateCount}개" +
        (model.HasFineDetailModel ? " · 원본 세부 획 검증" : "") +
        (InspectRotation ? " · 회전 검사: 전체 방향 탐색" : "") +
        (IsTrainingDirty ? " · 학습 설정 변경: 다시 학습하세요." : "");
    public string Status
    {
        get => _status; private set => Set(ref _status, value);
    }
    public string Results { get; private set; } = "검사 결과 없음";
    public bool ShowFeatures
    {
        get => _showFeatures;
        set
        {
            if (Set(ref _showFeatures, value))
            {
                if (_result is not null)
                {
                    _result = _result with
                    {
                        ShowFeatures = value
                    };
                    Changed?.Invoke();
                    return;
                }
                if (value)
                {
                    ScheduleLivePreview(trainingParametersChanged: true);
                }
                else
                {
                    CancelLivePreview();
                }
                Changed?.Invoke();
            }
        }
    }

    public bool ShowOverlay
    {
        get => _showOverlay;
        set
        {
            if (Set(ref _showOverlay, value))
            {
                if (!value)
                {
                    CancelLivePreview();
                }
                else if (_result is null && ShowFeatures)
                {
                    ScheduleLivePreview(trainingParametersChanged: true);
                }
                Changed?.Invoke();
            }
        }
    }

    public bool UseSelection
    {
        get => ActiveSearchSettings.UseSelection;
        set
        {
            if (ActiveSearchSettings.UseSelection == value &&
                _paperState.Search.UseSelection == value &&
                _templateState.Search.UseSelection == value &&
                _patMaxState.Search.UseSelection == value)
            {
                return;
            }
            var wasIdle = IsIdle;
            var hadTrainingPreview = _result?.Summary.Contains("미리보기", StringComparison.Ordinal) == true || _livePreview is not null;
            _paperState.Search = _paperState.Search with { UseSelection = value };
            _templateState.Search = _templateState.Search with { UseSelection = value };
            _patMaxState.Search = _patMaxState.Search with { UseSelection = value };
            foreach (var cpuState in _cpuStates.Values)
            {
                cpuState.Search = cpuState.Search with { UseSelection = value };
            }
            Notify(nameof(UseSelection));
            Notify(nameof(SearchFullImage));
            Notify(nameof(SearchScopeText));
            Invalidate();
            RefreshCommands();
            if (wasIdle && (hadTrainingPreview || !HasModel))
            {
                ScheduleLivePreview(trainingParametersChanged: false);
            }
        }
    }

    public bool SearchFullImage
    {
        get => !UseSelection;
        set => UseSelection = !value;
    }

    public ShapeMatchingViewModel(IShapeMatchingService service, ITemplateMatchingService? templateService = null,
        ICpuTemplateMatchingService? cpuService = null, IPatMaxMatchingService? patMaxService = null)
    {
        _service = service;
        _templateService = templateService;
        _cpuService = cpuService;
        _patMaxService = patMaxService;
        Train = new(TrainSelectedOrDefaultAsync, Failed, CanTrainCurrentRoi);
        Search = new(SearchAsync, Failed, () => IsIdle && _canExecute && _image is not null && HasModel && HasEnabledRegisteredPatterns && !IsTrainingDirty && OptionsError.Length == 0 && (!UseSelection || _selection is not null || _targetRoiCount > 0));
        Cancel = new(() => _cancellation?.Cancel(), () => _cancellation is not null);
        Clear = new(ClearModel, () => IsIdle && (HasModel || HasRegisteredPatterns));
        OpenPatternMaskEditor = new(() => PatternMaskEditorRequested?.Invoke(), () => IsIdle && _canExecute && _image is not null && _selection is not null);
        Locate = new(() =>
        {
            var points = Markers.SelectMany(marker => marker.Bounds).ToArray();
            LocateRequested?.Invoke(new(points.Min(p => p.X), points.Min(p => p.Y), points.Max(p => p.X), points.Max(p => p.Y)));
        }, () => _canExecute && IsIdle && !Markers.IsEmpty);
    }

    /// <summary>문서·선택이 바뀌었으면 결과를 버리고, 아니면 이전 결과를 그대로 둔다.</summary>
    /// <remarks>라이브 프레임마다 호출된다. 선택 해석은 <see cref="InspectionSelection"/>이 맡고 여기서는 할당을 만들지 않는다.</remarks>
    public void Update(InspectionUpdate update)
    {
        var state = update.State;
        var canExecute = update.CanExecute && update.ToolAvailable;
        var inputTransition = _inputAvailable != update.CanExecute || _canExecute != canExecute || _toolAvailable != update.ToolAvailable;
        if (ReferenceEquals(_lastRois, state.Rois) && ReferenceEquals(_lastSelection, state.Selection) &&
            _lastTransformPreview == update.TransformPreview &&
            _image == state.Image && _generation == update.Generation && _live == update.Live && !inputTransition)
        {
            return;
        }

        // 드래그 이동/회전/리사이즈 중(!update.CanExecute) 연속 마우스 이동 프레임에서는
        // 동기 UI 레이아웃/PropertyChanged 폭탄을 차단하고, '특징점 시각화(ShowFeatures)'가 켜진 경우
        // 기존 미리보기 오버레이는 화면에 유지한 채 백그라운드 단일 워커가 실시간으로 특징점과 가이드를 갱신하게 한다.
        if (!update.CanExecute && !_live && !inputTransition && _image == state.Image && _generation == update.Generation)
        {
            var dragSelection = InspectionSelection.Resolve(state, update.TransformPreview);
            var dragSelectionChanged = _selection != dragSelection.Selection || _selectionExclusion != dragSelection.Exclusion;
            _state = state;
            _lastRois = state.Rois;
            _lastSelection = state.Selection;
            _lastTransformPreview = update.TransformPreview;
            _selection = dragSelection.Selection;
            _searchSelection = dragSelection.SearchSelection;
            _selectionExclusion = dragSelection.Exclusion;
            _selectedRoiId = dragSelection.RoiId;
            _selectedRoiName = dragSelection.RoiName;
            _targetRoiCount = InspectionSelection.CountTargetLeafRois(state);
            if (dragSelectionChanged)
            {
                _previewShapeModel = null;
                _patMaxState.PreviewTemplate?.Dispose();
                _patMaxState.PreviewTemplate = null;
            }
            if (_result is not null || _cancellation is not null)
            {
                Invalidate();
            }
            if (dragSelectionChanged && _result is null && ShowFeatures)
            {
                ScheduleLivePreview(trainingParametersChanged: true);
            }
            return;
        }

        var selection = InspectionSelection.Resolve(state, update.TransformPreview);
        var targetRoiCount = InspectionSelection.CountTargetLeafRois(state);
        var wasDirty = IsTrainingDirty;
        if (!ReferenceEquals(_lastRois, state.Rois))
        {
            foreach (var mode in _cpuStates.Values)
            {
                if (mode.TeachingRoiId is { } cpuId && state.Rois.TryGetValue(cpuId, out var cpuRoi))
                {
                    mode.IsMaskDirty = !cpuRoi.Masks.IsEmpty;
                }
            }
            // 종이 템플릿은 학습 시점 마스크와 지금 마스크를 비교해야 오염 여부를 알 수 있다.
            if (_paperState.TeachingRoiId is { } paperId && state.Rois.TryGetValue(paperId, out var paperRoi))
            {
                _paperState.IsMaskDirty = !_paperState.TeachingMasks.SequenceEqual(paperRoi.Masks);
            }
            if (_templateState.TeachingRoiId is { } templateId && state.Rois.TryGetValue(templateId, out var templateRoi))
            {
                _templateState.IsMaskDirty = !_templateState.TeachingMasks.SequenceEqual(templateRoi.Masks);
            }
            if (_patMaxState.TeachingRoiId is { } patMaxId && state.Rois.TryGetValue(patMaxId, out var patMaxRoi))
            {
                _patMaxState.IsMaskDirty = !_patMaxState.TeachingMasks.SequenceEqual(patMaxRoi.Masks);
            }
            UpdateRegisteredPatternsMaskDirty(state);
        }
        var selectionChanged = _selection != selection.Selection || _selectionExclusion != selection.Exclusion;
        var finishedInteraction = !_inputAvailable && update.CanExecute;
        if (_image != state.Image || _generation != update.Generation || _live != update.Live ||
            (_training && _selection != selection.Selection) || (UseSelection && _searchSelection != selection.SearchSelection) ||
            (UseSelection && !ReferenceEquals(_lastSelection, state.Selection)) ||
            ((_training || UseSelection) && _selectionExclusion != selection.Exclusion) || wasDirty != IsTrainingDirty ||
            (_live && _inputAvailable && !update.CanExecute))
        {
            Invalidate();
        }
        _state = state;
        _lastRois = state.Rois;
        _lastSelection = state.Selection;
        _lastTransformPreview = update.TransformPreview;
        _image = state.Image;
        _generation = update.Generation;
        _selection = selection.Selection;
        _searchSelection = selection.SearchSelection;
        _selectionExclusion = selection.Exclusion;
        _selectedRoiId = selection.RoiId;
        _selectedRoiName = selection.RoiName;
        _targetRoiCount = targetRoiCount;
        _live = update.Live;
        _inputAvailable = update.CanExecute;
        _toolAvailable = update.ToolAvailable;
        _canExecute = canExecute;
        if (selectionChanged)
        {
            _previewShapeModel = null;
            _patMaxState.PreviewTemplate?.Dispose();
            _patMaxState.PreviewTemplate = null;
            _patMaxState.FilterPreview = null;
        }
        if ((selectionChanged || finishedInteraction) && _result is null && ShowFeatures)
        {
            var needsExtraction = IsPatMaxMethod
                ? (_patMaxState.PreviewTemplate is null && (IsTrainingDirty || _patMaxState.Template is null))
                : _previewShapeModel is null;
            ScheduleLivePreview(trainingParametersChanged: needsExtraction);
        }
        Notify(nameof(IsTrainingDirty));
        Notify(nameof(ModelSummary));
        Notify(nameof(PatMaxDiagnosticsText));
        RefreshCommands();
    }

    private async Task ExecuteTrainCoreAsync()
    {
        _training = true;
        if (IsPatMaxMethod)
        {
            await TrainPatMaxAsync();
            return;
        }
        if (IsCpuMethod)
        {
            await TrainCpuAsync();
            return;
        }
        if (!IsShapeMethod)
        {
            await TrainTemplateAsync();
            return;
        }
        var image = _image!;
        var roi = _selection!;
        var live = _live;
        var method = Method;
        var state = ActiveShapeState;
        var training = state.Training;
        var options = training.ToOptions();
        var exclusion = _selectionExclusion;
        var roiId = _selectedRoiId;
        var roiName = _selectedRoiName;
        await RunAsync(async (token, epoch) =>
        {
            Status = "원본 ROI 특징으로 모델 생성 중…";
            var output = await _service.TrainAsync(image, live, roi, options, token, exclusion);
            if (!Current(epoch, token, method))
            {
                return;
            }
            state.Model = output.Model;
            state.TeachingRegion = roi;
            state.TrainedSettings = training;
            state.TeachingRoiId = roiId;
            state.TeachingMasks = exclusion?.Regions ?? [];
            state.IsMaskDirty = false;
            RegisterOrUpdateTrainedPattern(
                method: method,
                roiId: roiId,
                roiName: roiName,
                teachingRegion: roi,
                teachingMasks: state.TeachingMasks,
                summary: $"{roi.Width:F0}×{roi.Height:F0} px · 특징 {output.Model.Levels[0].Features.Length}개 · {roi.Angle:F1}°",
                shapeModel: output.Model,
                shapeTrainedSettings: training,
                previewGray8: output.PreviewGray8,
                previewWidth: output.PreviewWidth,
                previewHeight: output.PreviewHeight);
            PresentTrainingPreview(output.Model, roi);
            Status = $"읽기/변환/마스크 {output.ReadMilliseconds:F1} ms · 학습 {output.TrainingMilliseconds:F1} ms";
            Notify(nameof(ModelSummary));
            Notify(nameof(Model));
            Notify(nameof(HasModel));
            Notify(nameof(IsTrainingDirty));
            Notify(nameof(Results));
            NotifyResultRows();
            Changed?.Invoke();
        });
    }

    private async Task SearchAsync()
    {
        var startedTimestamp = Stopwatch.GetTimestamp();
        if (ShouldUseMultiPatternSearch())
        {
            await SearchRegisteredPatternsAsync(startedTimestamp);
            return;
        }
        if (UseSelection && _selection is null && _targetRoiCount > 0)
        {
            await SearchMultipleRoisAsync(startedTimestamp);
            return;
        }
        if (IsPatMaxMethod)
        {
            await SearchPatMaxAsync();
            return;
        }
        if (IsCpuMethod)
        {
            await SearchCpuAsync(startedTimestamp);
            return;
        }
        if (!IsShapeMethod)
        {
            await SearchTemplateAsync();
            return;
        }
        var image = _image!;
        var method = Method;
        var state = ActiveShapeState;
        var model = state.Model!;
        var teachingRegion = state.TeachingRegion!;
        var roi = UseSelection ? _searchSelection : null;
        var exclusion = UseSelection ? _selectionExclusion : null;
        var live = _live;
        var options = CreateShapeSearchOptions();
        var rotation = ActiveRotationAcceptance;
        Guid? logId = null;
        if (method == PatternMatchingMethod.Shape)
        {
            logId = Guid.NewGuid();
            AddInspectionLog(new(logId.Value, DateTimeOffset.Now, Stopwatch.GetTimestamp(), image.Width, image.Height,
                _generation, model.Id, teachingRegion, roi, model.Options, options,
                model.Levels.Select(level => level.Features.Length).ToImmutableArray(), model.TemplateCount)
            {
                SearchTimeoutSeconds = SearchTimeoutSeconds,
                RotationAcceptance = rotation,
                DevicePreparationMilliseconds = options.ComputeMode == InspectionComputeMode.Cpu ? 0 : null
            });
        }
        await RunAsync(async (token, epoch) =>
        {
            var preparationMilliseconds = 0d;
            if (options.ComputeMode == InspectionComputeMode.GpuPreprocessing && _service is IShapeComputePreparation preparation)
            {
                Status = "CUDA 장치·커널 준비 중… 최초 준비 시간은 검사 시간과 별도 표시합니다.";
                UpdateInspectionLog(logId, log => log with { Stage = "device_preparation" });
                preparationMilliseconds = await preparation.PrepareGpuAsync(token);
                UpdateInspectionLog(logId, log => log with { DevicePreparationMilliseconds = preparationMilliseconds });
                if (!Current(epoch, token, method))
                {
                    UpdateInspectionLog(logId, log => log with { Outcome = "discarded" });
                    return;
                }
            }
            Status = options.ComputeMode == InspectionComputeMode.GpuPreprocessing
                ? "CUDA 후보 계산 + CPU 정밀 검사 중… 취소 가능" : "CPU 원본 해상도 검색 중… 취소 가능";
            UpdateInspectionLog(logId, log => log with { Stage = "search" });
            var serviceTimer = Stopwatch.StartNew();
            var output = await _service.SearchAsync(image, live, model, roi, options, token, exclusion);
            var serviceAwait = serviceTimer.Elapsed.TotalMilliseconds;
            UpdateInspectionLog(logId, log => log.WithOutput(output) with { ServiceAwaitMilliseconds = serviceAwait, Stage = "result_publish" });
            if (!Current(epoch, token, method) || !ReferenceEquals(model, state.Model))
            {
                UpdateInspectionLog(logId, log => log with { Outcome = "discarded" });
                return;
            }
            var decision = InspectionDecision.Evaluate(output, rotation, model.TrainingReferenceAngle);
            _inspectionNotice = decision.Reason;
            var boundary = SearchBoundary(image, roi);
            Verdict = decision.Verdict;
            _result = new($"{InspectionResultPresentation.Name(decision.Verdict)} · 검출 {output.Matches.Length}개",
                InspectionResultPresentation.Color(decision.Verdict), boundary, InspectionResultPresentation.Markers(model, teachingRegion, output, rotation), ShowFeatures)
            {
                BatchLabels = method == PatternMatchingMethod.Shape
            };
            var rows = output.Matches.Select((match, index) => FormatShapeMatch(match, index) +
                FormatRotationDecision(match, rotation, model.TrainingReferenceAngle, decision.Verdict == InspectionVerdict.Warning))
                .ToImmutableArray();
            Results = decision.Reason + (output.Matches.IsEmpty ? "\n검출 위치 없음" : "\n\n" + string.Join("\n\n", rows));
            var detailsEnd = Results.Length;
            Results += output.Matches.IsEmpty ? "" : "\n\n좌표: 원본 이미지 픽셀 · 각도: 이미지 기준 시계 방향";
            Results += output.Preprocessing is { Calls: > 0 } preprocessing
                ? $"\n\n{preprocessing.Backend} · {preprocessing.AdapterName}\nGPU 전처리 {preprocessing.Calls}회 · {preprocessing.ProcessedPixels:N0} px · 전송/대기 포함 {preprocessing.TotalMilliseconds:F1} ms"
                : "\n\n전처리·원본 정밀 검사: CPU";
            if (output.CandidateScoring is { } candidateScoring)
            {
                Results += $"\n{candidateScoring.Backend} · {candidateScoring.AdapterName}\nGPU 후보 계산 {candidateScoring.Calls}회 · 템플릿 {candidateScoring.TemplateCount:N0}개 · 전송/대기 포함 {candidateScoring.TotalMilliseconds:F1} ms";
            }
            if (preparationMilliseconds >= 1)
            {
                Results += $"\nCUDA 장치·커널 준비 {preparationMilliseconds:F1} ms (검사 시간과 별도, 세션에서 재사용)";
            }
            if (method == PatternMatchingMethod.Shape)
            {
                ResultRows = rows;
                _resultSummary = decision.Reason + Results[detailsEnd..];
            }
            Status = $"{output.Matches.Length}개 · {output.TotalMilliseconds:F1} ms (읽기/변환 {output.ReadMilliseconds:F1}) · 창 {output.Windows}개" +
                (output.CandidateScoring is not null ? " · CUDA + CPU" : output.Preprocessing is not null ? " · GPU 전처리" : " · CPU") +
                (output.Truncated ? " · 후보 예산 초과: 검색 불완전" : "") +
                (output.MatchLimitExceeded ? " · 최대 결과 수 도달: 일부 검출 결과 생략" : "");
            PublishResult();
            RecordSingleRoiPatternResult(
                _selectedRoiId,
                _selectedRoiName,
                roi,
                decision.Verdict,
                output.Matches.Length,
                output.Matches.IsEmpty ? 0.0 : output.Matches[0].Score,
                output.Matches.IsEmpty ? "검출 없음" : $"X={output.Matches[0].Pose.X:F2}, Y={output.Matches[0].Pose.Y:F2}, θ={RoiGeometry.NormalizeAngle(output.Matches[0].Pose.Angle * 180 / Math.PI):F2}°",
                output.TotalMilliseconds);
            UpdateInspectionLog(logId, log => log with
            {
                Outcome = output.Truncated || output.MatchLimitExceeded ? "incomplete" : "completed",
                Stage = "result_published",
                ResultPublishedMilliseconds = log.ElapsedMilliseconds,
                PresentationId = _result.PresentationId,
                OverlayVisible = ShowOverlay,
                RotationFailureCount = rotation is null || decision.Verdict == InspectionVerdict.Warning ? null :
                    output.Matches.Count(match => InspectionDecision.Evaluate(match, rotation, model.TrainingReferenceAngle).Verdict == InspectionVerdict.Fail)
            });
        }, logId);
    }

    private async Task RunAsync(Func<CancellationToken, long, Task> action, Guid? logId = null)
    {
        var trainingRoiId = _training ? _selectedRoiId : null;
        var completed = false;
        using var cancellation = new CancellationTokenSource();
        using var timeLimit = new CancellationTokenSource();
        using var combined = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token, timeLimit.Token);
        if (!_training && SearchTimeoutSeconds > 0)
        {
            timeLimit.CancelAfter(TimeSpan.FromSeconds(SearchTimeoutSeconds));
        }
        _cancellation = cancellation;
        var epoch = ++_epoch;
        PresentActivity(_training ? "학습 중 · 원본 ROI의 특징을 추출하고 있습니다." : "검사 중 · 원본 이미지에서 검색하고 있습니다. 취소 가능",
            _training ? [] : SearchBoundary(_image!, UseSelection ? _searchSelection : null));
        RefreshCommands();
        try
        {
            await action(combined.Token, epoch);
            combined.Token.ThrowIfCancellationRequested();
            completed = Current(epoch, combined.Token);
        }
        catch (OperationCanceledException) when (combined.IsCancellationRequested)
        {
            UpdateInspectionLog(logId, log => log with
            {
                Outcome = epoch != _epoch ? "discarded" :
                timeLimit.IsCancellationRequested && !cancellation.IsCancellationRequested ? "timeout" : "cancelled"
            });
            if (!_disposed && epoch == _epoch)
            {
                PresentWarning(timeLimit.IsCancellationRequested && !cancellation.IsCancellationRequested
                    ? "시간 한도 도달 · 검색 불완전 · 판정하지 않았습니다."
                    : "작업 취소 · 판정하지 않았습니다.");
            }
        }
        catch (Exception error)
        {
            UpdateInspectionLog(logId, log => log with { Outcome = "failed", ErrorType = error.GetType().FullName });
            if (!_disposed && epoch == _epoch)
            {
                var operation = _training ? "학습" : "검사";
                var interruption = timeLimit.IsCancellationRequested ? "시간 한도 도달 · 검색 불완전 · "
                    : cancellation.IsCancellationRequested ? "작업 취소 · " : "";
                PresentWarning(interruption + $"{operation} 실패 · " + error.Message + (HasModel && _training ? "\n이전 모델은 유지됩니다. 다시 학습한 뒤 검사하세요." : ""),
                    $"{operation} 실패 · 오른쪽 패턴 패널의 안내를 확인하세요.");
            }
        }
        finally
        {
            _training = false;
            _cancellation = null;
            RefreshCommands();
            UpdateInspectionLog(logId, log => log with { CommandMilliseconds = log.ElapsedMilliseconds }, true);
        }
        // 성공한 학습만 문서 변경을 요청한다. 실행/취소 수명 밖에서 편집 이력 한 건으로 적용한다.
        if (completed && trainingRoiId is { } id)
        {
            var trainingStatus = Status;
            RecordTrainedRoiAssignment(id, _selectedRoiName);
            SearchRangeRequested?.Invoke(id);
            if (_result is null && HasModel)
            {
                if (IsShapeMethod && Model is { } shapeModel && ActiveShapeState.TeachingRegion is { } taughtRoi)
                {
                    PresentTrainingPreview(shapeModel, taughtRoi);
                }
                else if (IsPatMaxMethod && _patMaxState.Template is { } patMaxTemplate)
                {
                    PresentPatMaxTrainingPreview(patMaxTemplate);
                }
                else if (IsCpuMethod && ActiveCpuState.Template is { } cpuTemplate)
                {
                    PresentCpuTrainingPreview(cpuTemplate);
                }
                else if (TemplateModel is { } templateModel && _templateState.TeachingRegion is { } templateRoi)
                {
                    PresentTemplateTrainingPreview(templateModel, templateRoi);
                }
                else
                {
                    PresentActivity("학습 완료 · 검사 실행을 누르세요.");
                }
                Status = trainingStatus;
            }
        }
    }

    private bool Current(long epoch, CancellationToken token) => !_disposed && !token.IsCancellationRequested && epoch == _epoch;

    private bool Current(long epoch, CancellationToken token, PatternMatchingMethod method)
        => Current(epoch, token) && Method == method;

    private void Invalidate(bool keepLivePreview = false)
    {
        _epoch++;
        _cancellation?.Cancel();
        CancelLivePreview(clearCurrentOverlay: !keepLivePreview);
        if (_result is not null || _cancellation is not null)
        {
            if (keepLivePreview && _livePreview is null && _result is not null)
            {
                _livePreview = _result;
            }
            ResetResult("입력 변경 · 다시 검사하세요.", clearLivePreview: !keepLivePreview);
        }
    }

    private void CancelLivePreview(bool clearCurrentOverlay = true)
    {
        Interlocked.Increment(ref _previewGeneration);
        Interlocked.Exchange(ref _pendingPreview, null);
        _previewCancellation?.Cancel();
        if (clearCurrentOverlay && _livePreview is not null)
        {
            _livePreview = null;
            Changed?.Invoke();
        }
    }

    private void ResetResult(string message, bool clearLivePreview = true)
    {
        ResetResultRows();
        _result = null;
        if (clearLivePreview)
        {
            _livePreview = null;
        }
        Verdict = null;
        Status = message;
        Results = message;
        PublishResult();
    }

    private static ImmutableArray<PointD> SearchBoundary(ImageReference image, RoiGeometry? region)
        => region?.Outline.ToImmutableArray() ?? [new(0, 0), new(image.Width, 0), new(image.Width, image.Height), new(0, image.Height)];

    private void PresentActivity(string message, ImmutableArray<PointD> boundary = default)
    {
        ResetResultRows();
        _livePreview = null;
        Verdict = null;
        Status = message;
        Results = message;
        _result = new(message, new(0.58f, 0.64f, 0.72f, 1), boundary.IsDefault ? [] : boundary, []);
        PublishResult();
    }

    private void PresentTrainingPreview(ShapeModel model, RoiGeometry teachingRegion)
    {
        ResetResultRows();
        _livePreview = null;
        _previewShapeModel = model;
        Verdict = null;
        var message = "학습 완료 · 추출된 특징점(초록: 일반 윤곽 / 하늘색: 비대칭 구분 키)을 확인하고 검사 실행을 누르세요.";
        Status = message;
        Results = message;
        _result = new("학습 완료 · 특징점 미리보기", new(0.58f, 0.64f, 0.72f, 1), [],
            InspectionResultPresentation.TrainingPreviewMarkers(model, teachingRegion, CreateShapeSearchOptions()), ShowFeatures: true);
        PublishResult();
    }

    private void PresentCpuTrainingPreview(CpuTemplate template)
    {
        ResetResultRows();
        _livePreview = null;
        Verdict = null;
        var message = "CPU 템플릿 등록 완료 · 추출된 특징/표본 미리보기를 확인하고 검사하세요.";
        Status = message;
        Results = message;
        var cpuOpts = ActiveCpuState.Options;
        _result = new("CPU 템플릿 · 특징점 미리보기", new(0.58f, 0.64f, 0.72f, 1), [],
            InspectionResultPresentation.CpuTrainingPreviewMarkers(template, cpuOpts.MinimumAngle, cpuOpts.MaximumAngle, cpuOpts.MinimumScale, cpuOpts.MaximumScale), ShowFeatures: true);
        PublishResult();
    }

    private void PresentTemplateTrainingPreview(CorrelationTemplate model, RoiGeometry teachingRegion)
    {
        ResetResultRows();
        _livePreview = null;
        Verdict = null;
        var message = "학습 완료 · 검사 실행으로 같은 모양의 위치를 찾으세요.";
        Status = message;
        Results = message;
        _result = new("OpenCV 템플릿 미리보기", new(0.58f, 0.64f, 0.72f, 1), [],
            InspectionResultPresentation.TemplateTrainingPreviewMarkers(model, teachingRegion), ShowFeatures: false);
        PublishResult();
    }

    private void ScheduleLivePreview(bool trainingParametersChanged = true)
    {
        if (!ShowFeatures || !ShowOverlay || !_toolAvailable || _disposed || !IsIdle || _image is null || OptionsError.Length > 0)
        {
            return;
        }
        var roi = _selection ?? (IsPatMaxMethod ? _patMaxState.Template?.TeachingRegion : IsShapeMethod ? ActiveShapeState.TeachingRegion : IsCpuMethod ? ActiveCpuState.Template?.TeachingRegion : null);
        if (roi is null)
        {
            return;
        }
        if (!trainingParametersChanged && IsPatMaxMethod)
        {
            var activePatMax = (!IsTrainingDirty ? _patMaxState.Template : null) ?? _patMaxState.PreviewTemplate;
            if (activePatMax is not null)
            {
                var s = _patMaxState.Settings;
                _livePreview = new(
                    "실시간 파라미터 미리보기 · PatMax 다이폴 · Coarse 프로브 · 인력장 대역",
                    new(0.58f, 0.64f, 0.72f, 1),
                    [],
                    InspectionResultPresentation.PatMaxTrainingPreviewMarkers(
                        activePatMax,
                        s.MinAngleDegrees,
                        s.MaxAngleDegrees,
                        s.EffectiveScaleMin,
                        s.EffectiveScaleMax,
                        s.ElasticityPixels),
                    ShowFeatures: true);
                NotifyPanelState();
                Notify(nameof(PatMaxDiagnosticsText));
                Changed?.Invoke();
                return;
            }
        }
        // 검색 파라미터(StrictContext, MaxOcclusionRatio, ClutterTolerancePixels 등)만 바뀐 경우에는
        // 이미 학습되었거나 직전 추출된 미리보기 모델을 재사용해 I/O 없이 즉시 캔버스 오버레이를 갱신한다.
        if (!trainingParametersChanged && IsShapeMethod)
        {
            var activeModel = (!IsTrainingDirty ? Model : null) ?? _previewShapeModel;
            if (activeModel is not null)
            {
                var targetRoi = !IsTrainingDirty && ActiveShapeState.TeachingRegion is { } taught ? taught : roi;
                _livePreview = new("실시간 파라미터 미리보기 · 특징점 및 문맥/허용거리 가이드", new(0.58f, 0.64f, 0.72f, 1), [],
                    InspectionResultPresentation.TrainingPreviewMarkers(activeModel, targetRoi, CreateShapeSearchOptions()), ShowFeatures: true);
                NotifyPanelState();
                Changed?.Invoke();
                return;
            }
        }
        if (!trainingParametersChanged && IsCpuMethod && !IsTrainingDirty && ActiveCpuState.Template is { } existingCpu)
        {
            var cpuOpts = ActiveCpuState.Options;
            _livePreview = new("실시간 파라미터 미리보기 · CPU 특징/회전/배율 가이드", new(0.58f, 0.64f, 0.72f, 1), [],
                InspectionResultPresentation.CpuTrainingPreviewMarkers(existingCpu, cpuOpts.MinimumAngle, cpuOpts.MaximumAngle, cpuOpts.MinimumScale, cpuOpts.MaximumScale), ShowFeatures: true);
            NotifyPanelState();
            Changed?.Invoke();
            return;
        }

        var epoch = Interlocked.Increment(ref _previewEpoch);
        var generation = Volatile.Read(ref _previewGeneration);
        Volatile.Write(ref _pendingPreview, new(_image, _live, roi, Method, _selectionExclusion, SynchronizationContext.Current, epoch, generation));
        if (_previewPump is null || _previewPump.IsCompleted)
        {
            _previewPump = RunLivePreviewPumpAsync();
        }
    }

    private async Task RunLivePreviewPumpAsync()
    {
        while (!_disposed)
        {
            var request = Interlocked.Exchange(ref _pendingPreview, null);
            if (request is null)
            {
                break;
            }
            if (_disposed || !IsIdle || !ShowFeatures || !ShowOverlay || !_toolAvailable ||
                request.Generation != Volatile.Read(ref _previewGeneration) || Method != request.Method)
            {
                continue;
            }
            using var current = new CancellationTokenSource();
            _previewCancellation = current;
            var token = current.Token;
            try
            {
                if (request.Method == PatternMatchingMethod.Shape)
                {
                    var options = ActiveShapeState.Training.ToOptions();
                    var searchOptions = CreateShapeSearchOptions();
                    var output = await _service.PreviewAsync(request.Image, request.Live, request.Roi, options, token, request.Exclusion).ConfigureAwait(false);
                    if (output is null || token.IsCancellationRequested || _disposed || !IsIdle || !ShowFeatures || !ShowOverlay || !_toolAvailable ||
                        request.Generation != Volatile.Read(ref _previewGeneration) || Method != request.Method || _result is not null)
                    {
                        continue;
                    }
                    var markers = InspectionResultPresentation.TrainingPreviewMarkers(output.Model, request.Roi, searchOptions);
                    var overlay = new ResultOverlay("실시간 파라미터 미리보기 · 특징점 및 회전/배율/허용거리 가이드", new(0.58f, 0.64f, 0.72f, 1), [], markers, ShowFeatures: true);
                    PublishLivePreview(request.UiContext, request.Epoch, request.Generation, request.Method, output.Model, overlay);
                }
                else if (IsCpuMethod && _cpuService is not null)
                {
                    var cpuMethod = CpuMethod;
                    var parameters = ActiveCpuState.Parameters;
                    var cpuOpts = ActiveCpuState.Options;
                    var template = await _cpuService.PreviewAsync(request.Image, request.Live, request.Roi, cpuMethod, token, request.Exclusion, parameters).ConfigureAwait(false);
                    if (template is null || token.IsCancellationRequested || _disposed || !IsIdle || !ShowFeatures || !ShowOverlay || !_toolAvailable ||
                        request.Generation != Volatile.Read(ref _previewGeneration) || Method != request.Method || _result is not null)
                    {
                        continue;
                    }
                    var markers = InspectionResultPresentation.CpuTrainingPreviewMarkers(template, cpuOpts.MinimumAngle, cpuOpts.MaximumAngle, cpuOpts.MinimumScale, cpuOpts.MaximumScale);
                    var overlay = new ResultOverlay("실시간 파라미터 미리보기 · CPU 특징/표본 미리보기", new(0.58f, 0.64f, 0.72f, 1), [], markers, ShowFeatures: true);
                    PublishLivePreview(request.UiContext, request.Epoch, request.Generation, request.Method, null, overlay);
                }
                else if (IsPatMaxMethod && _patMaxService is not null)
                {
                    var s = _patMaxState.Settings;
                    var trainParams = s.ToTrainParams();
                    var template = await _patMaxService.PreviewAsync(request.Image, request.Live, request.Roi, trainParams, token, request.Exclusion).ConfigureAwait(false);
                    if (template is null || token.IsCancellationRequested || _disposed || !IsIdle || !ShowFeatures || !ShowOverlay || !_toolAvailable ||
                        request.Generation != Volatile.Read(ref _previewGeneration) || Method != request.Method || _result is not null)
                    {
                        template?.Dispose();
                        continue;
                    }
                    var markers = InspectionResultPresentation.PatMaxTrainingPreviewMarkers(
                        template,
                        s.MinAngleDegrees,
                        s.MaxAngleDegrees,
                        s.EffectiveScaleMin,
                        s.EffectiveScaleMax,
                        s.ElasticityPixels);
                    var overlay = new ResultOverlay("실시간 파라미터 미리보기 · PatMax 다이폴 · Coarse 프로브 · 인력장 대역", new(0.58f, 0.64f, 0.72f, 1), [], markers, ShowFeatures: true);
                    PublishLivePatMaxPreview(request.UiContext, request.Epoch, request.Generation, request.Method, template, overlay);
                }
            }
            catch
            {
                // 실시간 미리보기 중 유효하지 않은 ROI/예산 초과/취소는 무시하고 본 학습·검사에서만 오류를 노출한다.
            }
            finally
            {
                if (ReferenceEquals(_previewCancellation, current))
                {
                    _previewCancellation = null;
                }
            }
        }
    }

    private void PublishLivePreview(SynchronizationContext? uiContext, long previewEpoch, long previewGeneration, PatternMatchingMethod method, ShapeModel? previewModel, ResultOverlay overlay)
    {
        void Apply()
        {
            if (_disposed || !IsIdle || !ShowFeatures || !ShowOverlay || !_toolAvailable ||
                previewGeneration != Volatile.Read(ref _previewGeneration) || previewEpoch < _lastPublishedPreviewEpoch || Method != method || _result is not null)
            {
                return;
            }
            _lastPublishedPreviewEpoch = previewEpoch;
            if (previewModel is not null)
            {
                _previewShapeModel = previewModel;
            }
            _livePreview = overlay;
            if (_inputAvailable)
            {
                Notify(nameof(ParameterPreviewSummary));
            }
            Changed?.Invoke();
        }

        if (uiContext is not null && SynchronizationContext.Current != uiContext)
        {
            uiContext.Post(_ => Apply(), null);
        }
        else
        {
            Apply();
        }
    }

    private void PublishLivePatMaxPreview(
        SynchronizationContext? uiContext,
        long previewEpoch,
        long previewGeneration,
        PatternMatchingMethod method,
        PatMaxTrainedTemplate previewTemplate,
        ResultOverlay overlay)
    {
        void Apply()
        {
            if (_disposed || !IsIdle || !ShowFeatures || !ShowOverlay || !_toolAvailable ||
                previewGeneration != Volatile.Read(ref _previewGeneration) || previewEpoch < _lastPublishedPreviewEpoch || Method != method || _result is not null)
            {
                previewTemplate.Dispose();
                return;
            }
            _lastPublishedPreviewEpoch = previewEpoch;
            if (!ReferenceEquals(_patMaxState.PreviewTemplate, previewTemplate))
            {
                _patMaxState.PreviewTemplate?.Dispose();
                _patMaxState.PreviewTemplate = previewTemplate;
            }
            _patMaxState.FilterPreview = BuildPatMaxFilterPreview(previewTemplate);
            _livePreview = overlay;
            if (_inputAvailable)
            {
                Notify(nameof(ParameterPreviewSummary));
                Notify(nameof(PatMaxDiagnosticsText));
                Notify(nameof(FilterPreview));
            }
            Changed?.Invoke();
        }

        if (uiContext is not null && SynchronizationContext.Current != uiContext)
        {
            uiContext.Post(_ => Apply(), null);
        }
        else
        {
            Apply();
        }
    }

    private void PresentWarning(string message, string? summary = null)
    {
        ResetResultRows();
        Verdict = InspectionVerdict.Warning;
        Results = message;
        Status = message;
        _inspectionNotice = message;
        _result = new("검사 확인 필요 · " + (summary ?? message), InspectionResultPresentation.Color(InspectionVerdict.Warning), [], []);
        PublishResult();
    }

    private void PublishResult()
    {
        Notify(nameof(Results));
        NotifyResultRows();
        Notify(nameof(VerdictText));
        Notify(nameof(VerdictColor));
        NotifyPanelState();
        Locate.Refresh();
        Changed?.Invoke();
    }

    private void Failed(Exception error)
    {
        if (!_disposed)
        {
            PresentWarning(error.Message);
        }
    }

    private void RefreshCommands()
    {
        Notify(nameof(IsIdle));
        Notify(nameof(IsBusy));
        Notify(nameof(VerdictText));
        Notify(nameof(OptionsError));
        NotifyPanelState();
        Train.Refresh();
        _registerGoodPatternCommand?.Refresh();
        _registerDefectPatternCommand?.Refresh();
        _toggleSelectedPatternRoleCommand?.Refresh();
        _removeSelectedPatternCommand?.Refresh();
        Search.Refresh();
        Cancel.Refresh();
        Clear.Refresh();
        OpenPatternMaskEditor.Refresh();
        Locate.Refresh();
        _patMaxAutoGranularityCommand?.Refresh();
    }

    public void Dispose()
    {
        _disposed = true;
        Invalidate();
        _patMaxState.PreviewTemplate?.Dispose();
        _patMaxState.PreviewTemplate = null;
        foreach (var item in RegisteredPatterns)
        {
            item.Dispose();
        }
        RegisteredPatterns.Clear();
        ActiveMethodPatterns.Clear();
        _patMaxState.Template?.Dispose();
        _patMaxState.Template = null;
    }
}
