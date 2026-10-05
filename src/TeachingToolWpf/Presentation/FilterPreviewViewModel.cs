using System.Globalization;
using VisionCanvas.Core.Editing;
using VisionCanvas.Imaging.Filters;

namespace TeachingToolWpf.Presentation;

public sealed record FilterOption(RoiFilterKind Kind, string Label)
{
    public override string ToString() => Label;
}

/// <summary>UI-owned preview session, with one worker and one replaceable pending request.</summary>
public sealed class FilterPreviewViewModel : ObservableObject, IDisposable
{
    private readonly IRoiFilterPreview _processor;
    private readonly EditorSession _editor;
    private DocumentState _document = DocumentState.Empty;
    private bool _live;

    private bool _active;

    private bool _busy;

    private bool _disposed;

    private bool _suppressed;

    private bool _editing;
    private RoiFilterKind _selectedFilter;
    private string _threshold = "128";

    private string _edgeLow = "50";

    private string _edgeHigh = "150";

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
    } = [new(RoiFilterKind.None, "없음 · 원본"), new(RoiFilterKind.Binarize, "Binarize · 이진화"), new(RoiFilterKind.Edge, "Edge · Canny")];

    public RoiFilterKind SelectedFilter
    {
        get => _selectedFilter;
        set
        {
            if (Set(ref _selectedFilter, value))
            {
                Notify(nameof(IsBinarize));
                Notify(nameof(IsEdge));
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

    public bool HasMixedFilters => _document.SelectionFacts.HasMixedFilters;

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
        Preview = new(Register, () => SelectedFilter != RoiFilterKind.None && _document.Selection.Count > 0 && !editor.IsPreviewing);
        Stop = new(StopPreview, () => IsActive);
        ClearRegistration = new(() =>
        {
            editor.SetFilters(new(RoiFilterKind.None));
            Update(editor.State, _live);
        },
            () => !editor.IsPreviewing && !_document.SelectionFacts.RegisteredFilters.IsEmpty);
    }

    public void Update(DocumentState state, bool isLive, RoiTransformPreview? preview = null)
    {
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
        _document = state;
        _live = isLive;
        _transformPreview = preview;
        Notify(nameof(HasMixedFilters));
        if (selectionChanged || settingsChanged)
        {
            _suppressed = false;
            var settings = state.SelectionFacts.First?.Filter ?? new(RoiFilterKind.None);
            Set(ref _selectedFilter, settings.Kind, nameof(SelectedFilter));
            Set(ref _threshold, settings.Threshold.ToString(CultureInfo.CurrentCulture), nameof(Threshold));
            Set(ref _edgeLow, settings.EdgeLow.ToString(CultureInfo.CurrentCulture), nameof(EdgeLow));
            Set(ref _edgeHigh, settings.EdgeHigh.ToString(CultureInfo.CurrentCulture), nameof(EdgeHigh));
            Notify(nameof(IsBinarize));
            Notify(nameof(IsEdge));
        }
        Schedule();
        RefreshCommands();
    }

    private void DraftChanged()
    {
        if (IsActive || SelectedFilter == RoiFilterKind.None)
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

        try
        {
            static double Number(string value) => double.Parse(value, CultureInfo.CurrentCulture);
            var settings = new FilterSettings(SelectedFilter, IsBinarize ? Number(Threshold) : 128,
                IsEdge ? Number(EdgeLow) : 50, IsEdge ? Number(EdgeHigh) : 150);
            settings.Validate();
            _editor.SetFilters(settings);
            _suppressed = false;
            Update(_editor.State, _live);
            Schedule(force: true);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            Status = "임계값을 확인하세요. 0–255 숫자와 Edge 하한 < 상한이 필요합니다.";
        }
    }

    private void RefreshCommands()
    {
        Preview?.Refresh();
        Stop?.Refresh();
        ClearRegistration?.Refresh();
    }

    private void ClearResult()
    {
        _result = null;
        Changed?.Invoke();
    }

    public void StopPreview()
    {
        _suppressed = true;
        IsActive = false;
        _revision++;
        _pending = _requested = null;
        _cancellation?.Cancel();
        ClearResult();
        Status = "원본 표시 · 등록 설정 유지 · 다시 선택하면 자동 미리보기";
        RefreshCommands();
    }

    private void Schedule(bool force = false, bool keepVisible = false)
    {
        if (_disposed)
        {
            return;
        }

        var rois = _document.SelectionFacts.RegisteredFilters;
        IsActive = !_suppressed && _document.Image is not null && rois.Length > 0;
        if (!IsActive)
        {
            _revision++;
            _pending = _requested = null;
            _cancellation?.Cancel();
            ClearResult();
            Status = _suppressed ? "원본 표시 · 등록 설정 유지 · 다시 선택하면 자동 미리보기" : "등록된 필터 없음 · 필터 선택 후 미리보기로 등록하세요.";
            return;
        }
        try
        {
            var next = new FilterRequest(_document.Image!, _live, rois, _transformPreview);
            if (!force && _requested is { } old && old.Image == next.Image && old.IsLive == next.IsLive &&
                old.Transform == next.Transform && old.Rois.SequenceEqual(next.Rois))
            {
                return;
            }

            _pending = _requested = next;
            _revision++;
            _cancellation?.Cancel();
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
            while (_pending is { } request && !_disposed)
            {
                _pending = null;
                var version = _revision;
                using var current = new CancellationTokenSource();
                _cancellation = current;
                try
                {
                    await Task.Delay(100, current.Token); // Coalesce rapid selection/property/drag changes.
                    var output = await _processor.ProcessAsync(request, current.Token);
                    if (_disposed || version != _revision || !IsActive)
                    {
                        continue;
                    }

                    _result = output;
                    Status = $"미리보기 켜짐 · {output.Regions.Count}개 ROI · {output.Milliseconds:F0} ms";
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
        _revision++;
        _pending = _requested = null;
        _cancellation?.Cancel();
        _result = null;
    }
}
