using VisionCanvas.Core.Editing;
using VisionCanvas.Imaging.Filters;
using VisionImaging.Abstractions;

namespace VisionCanvas.Filters.Wpf.ViewModels;

public sealed record FilterOption(RoiFilterKind Kind, string Label)
{
    public override string ToString() => Label;
}

/// <summary>UI-owned preview session, with one worker and one replaceable pending request.</summary>
public sealed partial class FilterPreviewViewModel : ObservableObject, IDisposable
{
    private const string RequestedCpuOnlyReason = "CpuOnly was requested.";

    private readonly IRoiFilterPreview _processor;
    private readonly EditorSession _editor;
    private DocumentState _document = DocumentState.Empty;
    private bool _live;
    private bool _active;
    private bool _busy;
    private bool _disposed;
    private bool _previewEnabled = true;
    private bool _editing;
    private RoiFilterKind _selectedFilter;
    private string _threshold = "128";
    private string _edgeLow = "50";
    private string _edgeHigh = "150";
    private string _radius = "3";
    private string _sigma = "1";
    private RoiFilterExecution _execution;
    private string _status = "필터를 선택한 뒤 미리보기를 누르세요.";
    private FilterRequest? _pending;
    private FilterRequest? _requested;
    private CancellationTokenSource? _cancellation;
    private long _revision;
    private Task? _pump;
    private FilterPreviewResult? _result;
    private RoiTransformPreview? _transformPreview;

    public IReadOnlyList<FilterOption> Options { get; } =
    [
        new(RoiFilterKind.None, "없음 · 원본"), new(RoiFilterKind.Binarize, "Binarize · 이진화 (OpenCV)"),
        new(RoiFilterKind.Edge, "Edge · Canny (OpenCV)"),
        new(RoiFilterKind.Gaussian, "Gaussian · 평활화"),
        new(RoiFilterKind.Erosion, "Erosion · 침식"), new(RoiFilterKind.Dilation, "Dilation · 팽창"),
        new(RoiFilterKind.Opening, "Opening · 열기"), new(RoiFilterKind.Closing, "Closing · 닫기"),
        new(RoiFilterKind.WhiteTopHat, "White Top-hat · 밝은 결함"), new(RoiFilterKind.BlackHat, "Black-hat · 어두운 결함"),
        new(RoiFilterKind.NativeThreshold, "Threshold · 원본 명암 임계값"),
        new(RoiFilterKind.GaussianBlackHatThreshold, "Gaussian → Black-hat → Threshold"),
        new(RoiFilterKind.Clahe, "[전처리] CLAHE · 적응형 히스토그램 대비 향상"),
        new(RoiFilterKind.GuidedFilter, "[전처리] Guided Filter · 엣지 보존 평활화"),
        new(RoiFilterKind.Bilateral, "[전처리] Bilateral · 양방향 노이즈 제거"),
        new(RoiFilterKind.Median, "[전처리] Median · 임펄스 점 노이즈 제거"),
        new(RoiFilterKind.AdaptiveThreshold, "[전처리] Adaptive Sauvola · 적분영상 지역 이진화"),
        new(RoiFilterKind.UnsharpMask, "[전처리] Unsharp Mask · 미세 엣지 선명화"),
        new(RoiFilterKind.SobelGradient, "[전처리] Sobel Gradient · 방향 경계 강도"),
        new(RoiFilterKind.WarpAffineShear, "[워핑] Affine & Shear · 회전/전단 보정"),
        new(RoiFilterKind.WarpPerspectiveKeystone, "[워핑] Perspective Keystone · 원근 사다리꼴 보정"),
        new(RoiFilterKind.WarpPolarUnwrap, "[워핑] Polar Ring Unwrap · 원형 링 직선 전개"),
        new(RoiFilterKind.WarpLensUndistort, "[워핑] Lens Undistort · 방사/접선 렌즈 왜곡 보정")
    ];

    public IReadOnlyList<RoiFilterExecution> ExecutionOptions { get; } = Enum.GetValues<RoiFilterExecution>();

    public RoiFilterKind SelectedFilter
    {
        get => _selectedFilter;
        set
        {
            if (Set(ref _selectedFilter, value))
            {
                NotifyFilterFields();
                DraftChanged();
            }
        }
    }

    public string Threshold
    {
        get => _threshold;
        set
        {
            if (Set(ref _threshold, value))
            {
                DraftChanged();
            }
        }
    }

    public string EdgeLow
    {
        get => _edgeLow;
        set
        {
            if (Set(ref _edgeLow, value))
            {
                DraftChanged();
            }
        }
    }

    public string EdgeHigh
    {
        get => _edgeHigh;
        set
        {
            if (Set(ref _edgeHigh, value))
            {
                DraftChanged();
            }
        }
    }

    public bool IsBinarize => FilterSettings.UsesByteThresholdKind(SelectedFilter);

    public bool IsEdge => SelectedFilter == RoiFilterKind.Edge;
    public bool IsIndustrial => FilterSettings.IsIndustrialKind(SelectedFilter);
    public bool UsesRadius => FilterSettings.UsesRadiusKind(SelectedFilter);
    public bool UsesSigma => FilterSettings.UsesSigmaKind(SelectedFilter);
    public bool UsesThreshold => IsBinarize || FilterSettings.UsesNativeThresholdKind(SelectedFilter);
    public string ThresholdCaption => SelectedFilter switch
    {
        RoiFilterKind.Binarize => "이진화 임계값 · 0–255",
        RoiFilterKind.WarpAffineShear => "어파인 회전 제어 · 0–255 (128 = 0°)",
        RoiFilterKind.WarpPerspectiveKeystone => "키스톤 수평 원근 제어 · 0–255 (128 = 대칭)",
        RoiFilterKind.WarpPolarUnwrap => "극좌표 링 내경 비율 · 0–255",
        RoiFilterKind.WarpLensUndistort => "렌즈 방사 왜곡 계수 k1 · 0–255 (128 = 0)",
        _ => "원본 명암 임계값 · Gray8 0–255 / Gray16 0–65535"
    };

    public string Radius
    {
        get => _radius;
        set
        {
            if (Set(ref _radius, value))
            {
                DraftChanged();
            }
        }
    }

    public string Sigma
    {
        get => _sigma;
        set
        {
            if (Set(ref _sigma, value))
            {
                DraftChanged();
            }
        }
    }

    public RoiFilterExecution Execution
    {
        get => _execution;
        set
        {
            if (Set(ref _execution, value))
            {
                DraftChanged();
            }
        }
    }

    private void NotifyFilterFields()
    {
        Notify(nameof(IsBinarize));
        Notify(nameof(IsEdge));
        Notify(nameof(IsIndustrial));
        Notify(nameof(UsesRadius));
        Notify(nameof(UsesSigma));
        Notify(nameof(UsesThreshold));
        Notify(nameof(ThresholdCaption));
        RefreshSliderValues();
        Notify(nameof(ThresholdMaximum));
    }

    public bool HasMixedFilters => _document.SelectionFacts.HasMixedFilters;

    public bool IsPreviewEnabled
    {
        get => _previewEnabled;
        set
        {
            if (Set(ref _previewEnabled, value))
            {
                Schedule(force: true, invalidate: true);
                RefreshCommands();
            }
        }
    }

    public bool IsActive
    {
        get => _active;
        private set => Set(ref _active, value);
    }

    public bool IsBusy
    {
        get => _busy;
        private set => Set(ref _busy, value);
    }

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public FilterPreviewResult? Result => _result;

    public event Action? Changed;

    public RelayCommand Preview { get; }

    public RelayCommand Apply { get; }

    public RelayCommand Stop { get; }

    public RelayCommand ClearRegistration { get; }

    public Task WhenIdle => _pump ?? Task.CompletedTask;

    public void RefreshLiveFrame()
    {
        if (IsActive && _live && !IsBusy)
        {
            Schedule(force: true, keepVisible: true);
        }
    }

    public FilterPreviewViewModel(IRoiFilterPreview processor, EditorSession editor)
    {
        _processor = processor;
        _editor = editor;
        Preview = new(() =>
        {
            IsPreviewEnabled = true;
            Register();
        }, () => SelectedFilter != RoiFilterKind.None && _document.Selection.Count > 0 && !editor.IsPreviewing);
        Apply = new(Register, () => _document.Selection.Count > 0 && !editor.IsPreviewing);
        BeginParameterEdit = new(StartParameterEdit, () => !_disposed && !_parameterEditing &&
            !editor.IsPreviewing && _document.Selection.Count > 0 && SelectedFilter != RoiFilterKind.None);
        EndParameterEdit = new(CompleteParameterEdit, () => _parameterEditing);
        CancelParameterEdit = new(CancelParameterChanges, () => _parameterEditing);
        Stop = new(StopPreview, () => IsActive);
        ClearRegistration = new(() =>
        {
            editor.SetFilters(new(RoiFilterKind.None));
            Update(editor.State, _live);
        },
            () => !editor.IsPreviewing && !_document.SelectionFacts.RegisteredFilters.IsEmpty);
    }

    public void Update(DocumentState state, bool isLive, RoiTransformPreview? preview = null, PixelKind? pixelKind = null)
    {
        UpdateInputMaximum(pixelKind);
        var editingChanged = _editing != _editor.IsPreviewing;
        _editing = _editor.IsPreviewing;
        if (IsViewOnlyNavigation(state, isLive, preview))
        {
            _document = state;
            if (editingChanged)
            {
                RefreshCommands();
            }
            return;
        }

        var selectionChanged = HasSelectionChanged(state);
        var settingsChanged = !selectionChanged && HasSelectedFilterSettingsChanged(state);
        var sourceChanged = _live != isLive;
        _document = state;
        _live = isLive;
        _transformPreview = preview;
        Notify(nameof(HasMixedFilters));
        if (selectionChanged || settingsChanged)
        {
            ResetParameterEdit();
            LoadSettings(state.SelectionFacts.First?.Filter ?? new(RoiFilterKind.None));
        }
        var invalidate = selectionChanged || settingsChanged || sourceChanged;
        Schedule(invalidate: invalidate, keepVisible: !invalidate);
        RefreshCommands();
    }

    private void UpdateInputMaximum(PixelKind? pixelKind)
    {
        int maximum;
        if (pixelKind is null)
        {
            maximum = _inputMaximum;
        }
        else if (pixelKind == PixelKind.Gray16)
        {
            maximum = 65535;
        }
        else
        {
            maximum = 255;
        }

        if (_inputMaximum != maximum)
        {
            _inputMaximum = maximum;
            Notify(nameof(ThresholdMaximum));
        }
    }

    private bool IsViewOnlyNavigation(DocumentState state, bool isLive, RoiTransformPreview? preview)
        => ReferenceEquals(_document.Rois, state.Rois) && ReferenceEquals(_document.Selection, state.Selection) &&
           _document.Image == state.Image && _live == isLive && _transformPreview == preview;

    private bool HasSelectionChanged(DocumentState state)
        => _document.Image != state.Image || !_document.Selection.SetEquals(state.Selection);

    private bool HasSelectedFilterSettingsChanged(DocumentState state)
        => !ReferenceEquals(_document.Rois, state.Rois) &&
           state.Selection.Any(id => !_document.Rois.TryGetValue(id, out var old) || old.Filter != state.Rois[id].Filter);

    private void DraftChanged()
    {
        RefreshSliderValues();
        if (_parameterEditing)
        {
            if (TryReadSettings(out var settings))
            {
                _draftSettings = settings;
                Schedule(force: true, keepVisible: true);
            }
        }
        else if (IsActive || SelectedFilter == RoiFilterKind.None || !_document.SelectionFacts.RegisteredFilters.IsEmpty)
        {
            Register();
        }

        RefreshCommands();
    }

    private void Register()
    {
        if (_disposed || _editor.IsPreviewing || _document.Selection.Count == 0)
        {
            return;
        }

        if (TryReadSettings(out var settings))
        {
            _editor.SetFilters(settings);
            Update(_editor.State, _live);
            Schedule(force: true, invalidate: true);
        }
    }

    private void RefreshCommands()
    {
        Preview?.Refresh();
        Apply?.Refresh();
        Stop?.Refresh();
        ClearRegistration?.Refresh();
        BeginParameterEdit?.Refresh();
        EndParameterEdit?.Refresh();
        CancelParameterEdit?.Refresh();
    }

    private void ClearResult()
    {
        if (_result is null)
        {
            return;
        }
        _result = null;
        Changed?.Invoke();
    }

    public void StopPreview()
    {
        IsPreviewEnabled = false;
    }

    private void Schedule(bool force = false, bool keepVisible = false, bool invalidate = false)
    {
        if (_disposed)
        {
            return;
        }

        IReadOnlyList<Roi> rois = _parameterRois ?? (IReadOnlyList<Roi>)_document.SelectionFacts.RegisteredFilters;
        IsActive = IsPreviewEnabled && _document.Image is not null && rois.Count > 0;
        if (!IsActive)
        {
            var idleStatus = !IsPreviewEnabled
                ? "미리보기 꺼짐 · 등록 설정 유지"
                : "등록된 필터 없음 · 필터 선택 후 설정을 적용하세요.";
            ResetPendingSchedule(idleStatus);
            return;
        }

        try
        {
            var next = new FilterRequest(_document.Image!, _live, rois, _transformPreview) { OverrideSettings = _draftSettings };
            if (!force && IsSameFilterRequest(_requested, next))
            {
                return;
            }

            QueueFilterRequest(next, keepVisible, invalidate);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            ResetPendingSchedule("임계값을 확인하세요. 0–255 숫자를 입력하세요.");
        }
    }

    private static bool IsSameFilterRequest(FilterRequest? previous, FilterRequest next) =>
        previous is not null &&
        previous.Image == next.Image &&
        previous.IsLive == next.IsLive &&
        previous.Transform == next.Transform &&
        previous.OverrideSettings == next.OverrideSettings &&
        previous.Rois.SequenceEqual(next.Rois);

    private void QueueFilterRequest(FilterRequest next, bool keepVisible, bool invalidate)
    {
        _pending = _requested = next;
        if (invalidate)
        {
            _revision++;
            _cancellation?.Cancel();
        }

        if (!keepVisible)
        {
            ClearResult();
            Status = "선택 영역을 처리하고 있습니다…";
        }

        if (_pump is null || _pump.IsCompleted)
        {
            _pump = RunAsync();
        }
    }

    private void ResetPendingSchedule(string status)
    {
        _revision++;
        _pending = _requested = null;
        _cancellation?.Cancel();
        ClearResult();
        Status = status;
    }

    private async Task RunAsync()
    {
        IsBusy = true;
        try
        {
            while (_pending is not null && !_disposed)
            {
                var version = _revision;
                using var current = new CancellationTokenSource();
                _cancellation = current;
                try
                {
                    await Task.Delay(100, current.Token);
                    if (_pending is not { } request)
                    {
                        continue;
                    }
                    _pending = null;
                    var output = await _processor.ProcessAsync(request, current.Token);
                    if (_disposed || version != _revision || !IsActive)
                    {
                        continue;
                    }

                    _result = output;
                    var state = _pending is null ? "미리보기 켜짐" : "갱신 중 · 마지막 완료 결과 표시";
                    Status = $"{state} · {output.Regions.Count}개 ROI · 전체 {output.Milliseconds:F0} ms\n{DescribeExecution(output)}";
                    Changed?.Invoke();
                }
                catch (OperationCanceledException) when (current.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {
                    if (version == _revision && !_disposed)
                    {
                        ClearResult();
                        Status = $"미리보기 실패: {ex.Message}";
                    }
                }
                finally
                {
                    if (ReferenceEquals(_cancellation, current))
                    {
                        _cancellation = null;
                    }
                }
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        ResetParameterEdit();
        _revision++;
        _pending = _requested = null;
        _cancellation?.Cancel();
        _result = null;
    }

    private static string DescribeExecution(FilterPreviewResult output)
    {
        var reports = output.Regions.Where(region => region.Execution is not null).Select(region => region.Execution!).ToArray();
        if (reports.Length == 0)
        {
            return "OpenCV · CPU";
        }
        var stages = reports.SelectMany(report => report.Stages).ToArray();
        var devices = string.Join(", ", stages.Select(stage => stage.Device == "CPU" ? "CPU" : "CUDA · " + stage.Device).Distinct());
        var fallback = string.Join("\n", stages.Where(stage => stage.Device == "CPU" && stage.Reason != RequestedCpuOnlyReason)
            .Select(stage => $"{stage.Filter}: CPU 전환 · {stage.Reason}").Distinct());
        var legacy = reports.Length < output.Regions.Count ? " + OpenCV CPU" : "";
        var memory = reports.Max(report => report.HostWorkingBytes + report.GpuWorkingBytes) / (1024d * 1024);
        var transfer = reports.Sum(report => report.UploadBytes + report.DownloadBytes) / (1024d * 1024);
        return $"IndustrialVision · {devices}{legacy}\n엔진 최대 작업 메모리 {memory:F1} MiB · 전송 {transfer:F1} MiB" +
            (fallback.Length == 0 ? "" : "\n" + fallback);
    }
}
