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
public sealed partial class ShapeMatchingViewModel : ObservableObject, IDisposable
{
    private readonly IShapeMatchingService _service;
    private ImageReference? _image;
    private RoiGeometry? _selection;
    private RoiGeometry? _searchSelection;
    private RoiExclusion? _selectionExclusion;
    private Guid? _selectedRoiId;
    private long _generation;
    private bool _live;
    private bool _canExecute;
    private bool _inputAvailable;
    private bool _disposed;
    private CancellationTokenSource? _cancellation;
    private long _epoch;
    private string _status = "ROI 하나를 선택해 모델을 학습하세요. 모델은 이 세션에서 이미지 교체 후에도 유지됩니다.";
    private bool _training;
    private bool _showFeatures;
    private bool _showOverlay = true;
    private ResultOverlay? _result;

    public event Action<BoundsD>? LocateRequested;

    public event Action? Changed;
    public AsyncCommand Train
    {
        get;
    }
    public AsyncCommand Search
    {
        get;
    }
    public RelayCommand Cancel
    {
        get;
    }
    public RelayCommand Clear
    {
        get;
    }
    public bool IsIdle => !_disposed && _cancellation is null;
    public bool IsBusy => _cancellation is not null;
    public ShapeModel? Model => ActiveShapeState.Model;
    public ImmutableArray<MatchMarker> Markers => _result?.Matches ?? [];
    public ResultOverlay? Overlay => ShowOverlay ? _result : null;
    public InspectionVerdict? Verdict
    {
        get; private set;
    }
    public string VerdictText => Verdict is { } verdict ? InspectionResultPresentation.Name(verdict) : IsIdle ? "검사 대기" : "처리 중";
    public string VerdictColor => Verdict is { } verdict ? InspectionResultPresentation.Hex(verdict) : "#94A3B8";
    public RelayCommand Locate
    {
        get;
    }
    public string ModelSummary => IsCpuMethod ? CpuSummary : IsTdcmMethod ? TdcmSummary : !IsShapeMethod ? TemplateSummary : Model is not { } model ? "학습된 모델 없음" :
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
                Changed?.Invoke();
            }
        }
    }

    public bool UseSelection
    {
        get => ActiveSearchSettings.UseSelection;
        set => ChangeSearchSettings(ActiveSearchSettings with { UseSelection = value });
    }

    public ShapeMatchingViewModel(IShapeMatchingService service, ITemplateMatchingService? templateService = null, ITdcmMatchingService? tdcmService = null,
        ICpuTemplateMatchingService? cpuService = null)
    {
        _service = service;
        _templateService = templateService;
        _tdcmService = tdcmService;
        _cpuService = cpuService;
        Train = new(TrainAsync, Failed, () => IsIdle && _canExecute && _image is not null && _selection is not null && OptionsError.Length == 0 && (IsCpuMethod ? _cpuService is not null : IsShapeMethod || (IsTdcmMethod ? _tdcmService is not null : _templateService is not null)));
        Search = new(SearchAsync, Failed, () => IsIdle && _canExecute && _image is not null && HasModel && !IsTrainingDirty && OptionsError.Length == 0 && (!UseSelection || _selection is not null));
        Cancel = new(() => _cancellation?.Cancel(), () => _cancellation is not null);
        Clear = new(ClearModel, () => IsIdle && HasModel);
        Locate = new(() =>
        {
            var points = Markers.SelectMany(marker => marker.Bounds).ToArray();
            LocateRequested?.Invoke(new(points.Min(p => p.X), points.Min(p => p.Y), points.Max(p => p.X), points.Max(p => p.Y)));
        }, () => _canExecute && IsIdle && !Markers.IsEmpty);
    }

    public void Update(DocumentState state, long generation, bool live, bool canExecute, bool toolAvailable = true)
    {
        var selected = state.Selection.Count == 1 ? state.Rois[state.Selection.Single()] : null;
        var selection = selected is not null && !state.IsGroupRoi(selected.Id) ? selected.Geometry : null;
        var searchSelection = selection is null ? null : selected!.SearchRange?.Expand(selection) ?? selection;
        var exclusion = selection is not null && !selected!.Masks.IsEmpty ? new RoiExclusion(selection, selected.Masks) : null;
        var wasDirty = IsTrainingDirty;
        foreach (var mode in _cpuStates.Values)
        {
            if (mode.TeachingRoiId is { } cpuId && state.Rois.TryGetValue(cpuId, out var cpuRoi))
            {
                mode.IsMaskDirty = !cpuRoi.Masks.IsEmpty;
            }
        }
        if (_tdcmTeachingId is { } tdcmId && state.Rois.TryGetValue(tdcmId, out var tdcmRoi))
        {
            _tdcmMaskDirty = !tdcmRoi.Masks.IsEmpty;
        }
        foreach (var mode in new[] { _paperState })
        {
            if (mode.TeachingRoiId is { } id && state.Rois.TryGetValue(id, out var trained))
            {
                mode.IsMaskDirty = !mode.TeachingMasks.SequenceEqual(trained.Masks);
            }
        }
        if (_templateState.TeachingRoiId is { } templateId && state.Rois.TryGetValue(templateId, out var templateRoi))
        {
            _templateState.IsMaskDirty = !_templateState.TeachingMasks.SequenceEqual(templateRoi.Masks);
        }
        if (_image != state.Image || _generation != generation || _live != live ||
            (_training && _selection != selection) || (UseSelection && _searchSelection != searchSelection) ||
            ((_training || UseSelection) && _selectionExclusion != exclusion) || wasDirty != IsTrainingDirty ||
            (_live && _inputAvailable && !canExecute))
        {
            Invalidate();
        }
        _image = state.Image;
        _generation = generation;
        _selection = selection;
        _searchSelection = searchSelection;
        _selectionExclusion = exclusion;
        _selectedRoiId = selection is null ? null : selected!.Id;
        _live = live;
        _inputAvailable = canExecute;
        _canExecute = canExecute && toolAvailable;
        Notify(nameof(IsTrainingDirty));
        Notify(nameof(ModelSummary));
        RefreshCommands();
    }

    private async Task TrainAsync()
    {
        _training = true;
        if (IsCpuMethod)
        {
            await TrainCpuAsync();
            return;
        }
        if (IsTdcmMethod)
        {
            await TrainTdcmAsync();
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
            PresentActivity("학습 완료 · 검사 실행을 누르세요.");
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
        if (IsCpuMethod)
        {
            await SearchCpuAsync(startedTimestamp);
            return;
        }
        if (IsTdcmMethod)
        {
            await SearchTdcmAsync(startedTimestamp);
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
    }

    private bool Current(long epoch, CancellationToken token) => !_disposed && !token.IsCancellationRequested && epoch == _epoch;

    private bool Current(long epoch, CancellationToken token, PatternMatchingMethod method)
        => Current(epoch, token) && Method == method;

    private void Invalidate()
    {
        _epoch++;
        _cancellation?.Cancel();
        if (_result is not null || _cancellation is not null)
        {
            ResetResult("입력 변경 · 다시 검사하세요.");
        }
    }

    private void ResetResult(string message)
    {
        ResetResultRows();
        _result = null;
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
        Verdict = null;
        Status = message;
        Results = message;
        _result = new(message, new(0.58f, 0.64f, 0.72f, 1), boundary.IsDefault ? [] : boundary, []);
        PublishResult();
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
        Search.Refresh();
        Cancel.Refresh();
        Clear.Refresh();
        Locate.Refresh();
    }

    public void Dispose()
    {
        _disposed = true;
        Invalidate();
    }
}
