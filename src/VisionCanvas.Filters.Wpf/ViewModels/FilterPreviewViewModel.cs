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

    public IReadOnlyList<FilterOption> Options
    {
        get;
    } =
    [
        new(RoiFilterKind.None, "없음 · 원본"), new(RoiFilterKind.Binarize, "Binarize · 이진화 (OpenCV)"),
        new(RoiFilterKind.Edge, "Edge · Canny (OpenCV)"),
        new(RoiFilterKind.Gaussian, "Gaussian · 평활화"),
        new(RoiFilterKind.Erosion, "Erosion · 침식"), new(RoiFilterKind.Dilation, "Dilation · 팽창"),
        new(RoiFilterKind.Opening, "Opening · 열기"), new(RoiFilterKind.Closing, "Closing · 닫기"),
        new(RoiFilterKind.WhiteTopHat, "White Top-hat · 밝은 결함"), new(RoiFilterKind.BlackHat, "Black-hat · 어두운 결함"),
        new(RoiFilterKind.NativeThreshold, "Threshold · 원본 명암 임계값"),
        new(RoiFilterKind.GaussianBlackHatThreshold, "Gaussian → Black-hat → Threshold")
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

    public bool IsBinarize => SelectedFilter == RoiFilterKind.Binarize;

    public bool IsEdge => SelectedFilter == RoiFilterKind.Edge;
    public bool IsIndustrial => new FilterSettings(SelectedFilter).IsIndustrial;
    public bool UsesRadius => new FilterSettings(SelectedFilter).UsesRadius;
    public bool UsesSigma => new FilterSettings(SelectedFilter).UsesSigma;
    public bool UsesThreshold => IsBinarize || new FilterSettings(SelectedFilter).UsesNativeThreshold;
    public string ThresholdCaption => IsBinarize ? "이진화 임계값 · 0–255" : "원본 명암 임계값 · Gray8 0–255 / Gray16 0–65535";

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

    public RelayCommand Preview
    {
        get;
    }

    public RelayCommand Apply
    {
        get;
    }

    public RelayCommand Stop
    {
        get;
    }

    public RelayCommand ClearRegistration
    {
        get;
    }

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
        var maximum = pixelKind is null ? _inputMaximum : pixelKind == PixelKind.Gray16 ? 65535 : 255;
        if (_inputMaximum != maximum)
        {
            _inputMaximum = maximum;
            Notify(nameof(ThresholdMaximum));
        }
        var editingChanged = _editing != _editor.IsPreviewing;
        _editing = _editor.IsPreviewing;
        if (ReferenceEquals(_document.Rois, state.Rois) && ReferenceEquals(_document.Selection, state.Selection) && _document.Image == state.Image && _live == isLive && _transformPreview == preview)
        {
            _document = state;
            if (editingChanged)
            {
                RefreshCommands();
            }

            return;
        } // View-only navigation cannot change image-local filter pixels.
        var selectionChanged = _document.Image != state.Image || !_document.Selection.SetEquals(state.Selection);
        var settingsChanged = !selectionChanged && !ReferenceEquals(_document.Rois, state.Rois) &&
            state.Selection.Any(id => !_document.Rois.TryGetValue(id, out var old) || old.Filter != state.Rois[id].Filter);
        var sourceChanged = _live != isLive;
        _document = state;
        _live = isLive;
        _transformPreview = preview;
        Notify(nameof(HasMixedFilters));
        if (selectionChanged || settingsChanged)
        {
            ResetParameterEdit();
            var settings = state.SelectionFacts.First?.Filter ?? new(RoiFilterKind.None);
            LoadSettings(settings);
        }
        var invalidate = selectionChanged || settingsChanged || sourceChanged;
        Schedule(invalidate: invalidate, keepVisible: !invalidate);
        RefreshCommands();
    }

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
            _revision++;
            _pending = _requested = null;
            _cancellation?.Cancel();
            ClearResult();
            Status = !IsPreviewEnabled ? "미리보기 꺼짐 · 등록 설정 유지" : "등록된 필터 없음 · 필터 선택 후 설정을 적용하세요.";
            return;
        }
        try
        {
            var next = new FilterRequest(_document.Image!, _live, rois, _transformPreview) { OverrideSettings = _draftSettings };
            if (!force && _requested is { } old && old.Image == next.Image && old.IsLive == next.IsLive &&
                old.Transform == next.Transform && old.OverrideSettings == next.OverrideSettings && old.Rois.SequenceEqual(next.Rois))
            {
                return;
            }

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
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            _revision++;
            _pending = _requested = null;
            _cancellation?.Cancel();
            ClearResult();
            Status = "임계값을 확인하세요. 0–255 숫자를 입력하세요.";
        }
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
                    // A bounded refresh interval, not a debounce restarted on every pointer event.
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
        var fallback = string.Join("\n", stages.Where(stage => stage.Device == "CPU" && stage.Reason != "CpuOnly was requested.")
            .Select(stage => $"{stage.Filter}: CPU 전환 · {stage.Reason}").Distinct());
        var legacy = reports.Length < output.Regions.Count ? " + OpenCV CPU" : "";
        var memory = reports.Max(report => report.HostWorkingBytes + report.GpuWorkingBytes) / (1024d * 1024);
        var transfer = reports.Sum(report => report.UploadBytes + report.DownloadBytes) / (1024d * 1024);
        return $"IndustrialVision · {devices}{legacy}\n엔진 최대 작업 메모리 {memory:F1} MiB · 전송 {transfer:F1} MiB" +
            (fallback.Length == 0 ? "" : "\n" + fallback);
    }
}
