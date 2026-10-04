using System.Collections.Immutable;
using TeachingToolWpf.Core.Editing;
using TeachingToolWpf.Core.Geometry;
using TeachingToolWpf.Imaging.Matching;
using TeachingToolWpf.Rendering;
using VisionAlgorithm.OpenSrc.PatternMatching;

namespace TeachingToolWpf.Presentation;

/// <summary>세션의 불변 모델과 일회 검사 결과. ROI 문서/편집 이력은 변경하지 않는다.</summary>
public sealed partial class ShapeMatchingViewModel : ObservableObject, IDisposable
{
    private readonly IShapeMatchingService _service;
    private ImageReference? _image;
    private RoiGeometry? _selection;
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
    public string ModelSummary => !IsShapeMethod ? TemplateSummary : Model is not { } model ? "학습된 모델 없음" :
        $"특징 {model.Levels[0].Features.Length}개 · {model.Levels.Length}단계(자동) · 템플릿 {model.TemplateCount}개" +
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

    public ShapeMatchingViewModel(IShapeMatchingService service, ITemplateMatchingService? templateService = null)
    {
        _service = service;
        _templateService = templateService;
        Train = new(TrainAsync, Failed, () => IsIdle && _canExecute && _image is not null && _selection is not null && OptionsError.Length == 0 && (IsShapeMethod || _templateService is not null));
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
        var selection = state.Selection.Count == 1 ? state.Rois[state.Selection.Single()].Geometry : null;
        if (_image != state.Image || _generation != generation || _live != live ||
            ((UseSelection || _training) && _selection != selection) ||
            (_live && _inputAvailable && !canExecute))
        {
            Invalidate();
        }
        _image = state.Image;
        _generation = generation;
        _selection = selection;
        _live = live;
        _inputAvailable = canExecute;
        _canExecute = canExecute && toolAvailable;
        RefreshCommands();
    }

    private async Task TrainAsync()
    {
        _training = true;
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
        var options = training.ToOptions(IsGeometricMethod);
        await RunAsync(async (token, epoch) =>
        {
            Status = "원본 ROI 특징으로 모델 생성 중…";
            var output = await _service.TrainAsync(image, live, roi, options, token);
            if (!Current(epoch, token, method))
            {
                return;
            }
            state.Model = output.Model;
            state.TeachingRegion = roi;
            state.TrainedSettings = training;
            PresentActivity("학습 완료 · 검사 실행을 누르세요.");
            Status = $"읽기/변환/마스크 {output.ReadMilliseconds:F1} ms · 학습 {output.TrainingMilliseconds:F1} ms";
            Notify(nameof(ModelSummary));
            Notify(nameof(Model));
            Notify(nameof(HasModel));
            Notify(nameof(IsTrainingDirty));
            Notify(nameof(Results));
            Changed?.Invoke();
        });
    }

    private async Task SearchAsync()
    {
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
        var roi = UseSelection ? _selection : null;
        var live = _live;
        var options = CreateShapeSearchOptions();
        await RunAsync(async (token, epoch) =>
        {
            Status = options.ComputeMode == InspectionComputeMode.GpuPreprocessing
                ? "GPU 전처리 + CPU 원본 검색 중… 취소 가능" : "CPU 원본 해상도 검색 중… 취소 가능";
            var output = await _service.SearchAsync(image, live, model, roi, options, token);
            if (!Current(epoch, token, method) || !ReferenceEquals(model, state.Model))
            {
                return;
            }
            var decision = InspectionDecision.Evaluate(output);
            var boundary = SearchBoundary(image, roi);
            Verdict = decision.Verdict;
            _result = new($"{InspectionResultPresentation.Name(decision.Verdict)} · 검출 {output.Matches.Length}개",
                InspectionResultPresentation.Color(decision.Verdict), boundary, InspectionResultPresentation.Markers(model, teachingRegion, output), ShowFeatures);
            Results = decision.Reason + (output.Matches.IsEmpty ? "\n검출 위치 없음" : "\n\n" + string.Join("\n\n", output.Matches.Select((match, index) => FormatShapeMatch(match, index, method == PatternMatchingMethod.GeometricShape))));
            Results += output.Matches.IsEmpty ? "" : "\n\n좌표: 원본 이미지 픽셀 · 각도: 이미지 기준 시계 방향";
            Results += output.Preprocessing is { } preprocessing
                ? $"\n\n{preprocessing.Backend} · {preprocessing.AdapterName}\nGPU 전처리 {preprocessing.Calls}회 · {preprocessing.ProcessedPixels:N0} px · 전송/대기 포함 {preprocessing.TotalMilliseconds:F1} ms"
                : "\n\n실행 장치: CPU";
            Status = $"{output.Matches.Length}개 · {output.TotalMilliseconds:F1} ms (읽기/변환 {output.ReadMilliseconds:F1}) · 창 {output.Windows}개" +
                (output.Preprocessing is not null ? " · GPU 전처리" : " · CPU") +
                (output.Truncated ? " · 후보 예산 초과: 검색 불완전" : "") +
                (output.MatchLimitExceeded ? " · 최대 결과 수 도달: 일부 검출 결과 생략" : "");
            PublishResult();
        });
    }

    private async Task RunAsync(Func<CancellationToken, long, Task> action)
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
            _training ? [] : SearchBoundary(_image!, UseSelection ? _selection : null));
        RefreshCommands();
        try
        {
            await action(combined.Token, epoch);
            combined.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (combined.IsCancellationRequested)
        {
            if (!_disposed && epoch == _epoch)
            {
                PresentWarning(timeLimit.IsCancellationRequested && !cancellation.IsCancellationRequested
                    ? "시간 한도 도달 · 검색 불완전 · 판정하지 않았습니다."
                    : "작업 취소 · 판정하지 않았습니다.");
            }
        }
        catch (Exception error)
        {
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
        Verdict = null;
        Status = message;
        Results = message;
        _result = new(message, new(0.58f, 0.64f, 0.72f, 1), boundary.IsDefault ? [] : boundary, []);
        PublishResult();
    }

    private void PresentWarning(string message, string? summary = null)
    {
        Verdict = InspectionVerdict.Warning;
        Results = message;
        Status = message;
        _result = new("WARN · " + (summary ?? message), InspectionResultPresentation.Color(InspectionVerdict.Warning), [], []);
        PublishResult();
    }

    private void PublishResult()
    {
        Notify(nameof(Results));
        Notify(nameof(VerdictText));
        Notify(nameof(VerdictColor));
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
