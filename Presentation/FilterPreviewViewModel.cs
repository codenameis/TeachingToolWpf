using System.Globalization;
using TeachingToolWpf.Core.Editing;
using TeachingToolWpf.Imaging.Filters;

namespace TeachingToolWpf.Presentation;

public sealed record FilterOption(RoiFilterKind Kind, string Label)
{
    public override string ToString() => Label;
}

/// <summary>UI-owned preview session, with one worker and one replaceable pending request.</summary>
public sealed class FilterPreviewViewModel : ObservableObject, IDisposable
{
    private readonly IRoiFilterPreview processor;
    private readonly EditorSession editor;
    private DocumentState document = DocumentState.Empty;
    private bool live, active, busy, disposed, suppressed, editing;
    private RoiFilterKind selectedFilter;
    private string threshold = "128", edgeLow = "50", edgeHigh = "150", status = "필터를 선택한 뒤 미리보기를 누르세요.";
    private FilterRequest? pending, requested;
    private CancellationTokenSource? cancellation;
    private long revision;
    private Task? pump;
    private FilterPreviewResult? result;
    private RoiTranslationPreview? translation;
    public IReadOnlyList<FilterOption> Options { get; } = [new(RoiFilterKind.None, "없음 · 원본"), new(RoiFilterKind.Binarize, "Binarize · 이진화"), new(RoiFilterKind.Edge, "Edge · Canny")];
    public RoiFilterKind SelectedFilter { get => selectedFilter; set { if (Set(ref selectedFilter, value)) { Notify(nameof(IsBinarize)); Notify(nameof(IsEdge)); DraftChanged(); } } }
    public string Threshold { get => threshold; set { if (Set(ref threshold, value)) DraftChanged(); } }
    public string EdgeLow { get => edgeLow; set { if (Set(ref edgeLow, value)) DraftChanged(); } }
    public string EdgeHigh { get => edgeHigh; set { if (Set(ref edgeHigh, value)) DraftChanged(); } }
    public bool IsBinarize => SelectedFilter == RoiFilterKind.Binarize;
    public bool IsEdge => SelectedFilter == RoiFilterKind.Edge;
    public bool HasMixedFilters => document.SelectionFacts.HasMixedFilters;
    public bool IsActive { get => active; private set => Set(ref active, value); }
    public bool IsBusy { get => busy; private set => Set(ref busy, value); }
    public string Status { get => status; private set => Set(ref status, value); }
    public FilterPreviewResult? Result => result;
    public event Action? Changed;
    public RelayCommand Preview { get; }
    public RelayCommand Stop { get; }
    public RelayCommand ClearRegistration { get; }
    public Task WhenIdle => pump ?? Task.CompletedTask;
    public void RefreshLiveFrame() { if (IsActive && live && !IsBusy) Schedule(force: true, keepVisible: true); }

    public FilterPreviewViewModel(IRoiFilterPreview processor, EditorSession editor)
    {
        this.processor = processor; this.editor = editor;
        Preview = new(Register, () => SelectedFilter != RoiFilterKind.None && document.Selection.Count > 0 && !editor.IsPreviewing);
        Stop = new(StopPreview, () => IsActive);
        ClearRegistration = new(() => { editor.SetFilters(new(RoiFilterKind.None)); Update(editor.State, live); },
            () => !editor.IsPreviewing && !document.SelectionFacts.RegisteredFilters.IsEmpty);
    }
    public void Update(DocumentState state, bool isLive, RoiTranslationPreview? preview = null)
    {
        var editingChanged = editing != editor.IsPreviewing; editing = editor.IsPreviewing;
        if (ReferenceEquals(document.Rois, state.Rois) && ReferenceEquals(document.Selection, state.Selection) && document.Image == state.Image && live == isLive && translation == preview)
        { document = state; if (editingChanged) RefreshCommands(); return; } // View-only navigation cannot change image-local filter pixels.
        var selectionChanged = document.Image != state.Image || !document.Selection.SetEquals(state.Selection);
        var settingsChanged = !selectionChanged && !ReferenceEquals(document.Rois, state.Rois) &&
            state.Selection.Any(id => !document.Rois.TryGetValue(id, out var old) || old.Filter != state.Rois[id].Filter);
        document = state; live = isLive; translation = preview;
        Notify(nameof(HasMixedFilters));
        if (selectionChanged || settingsChanged)
        {
            suppressed = false;
            var settings = state.SelectionFacts.First?.Filter ?? new(RoiFilterKind.None);
            Set(ref selectedFilter, settings.Kind, nameof(SelectedFilter));
            Set(ref threshold, settings.Threshold.ToString(CultureInfo.CurrentCulture), nameof(Threshold));
            Set(ref edgeLow, settings.EdgeLow.ToString(CultureInfo.CurrentCulture), nameof(EdgeLow));
            Set(ref edgeHigh, settings.EdgeHigh.ToString(CultureInfo.CurrentCulture), nameof(EdgeHigh));
            Notify(nameof(IsBinarize)); Notify(nameof(IsEdge));
        }
        Schedule(); RefreshCommands();
    }
    private void DraftChanged()
    {
        if (IsActive || SelectedFilter == RoiFilterKind.None) Register();
        RefreshCommands();
    }
    private void Register()
    {
        if (disposed || editor.IsPreviewing || document.Selection.Count == 0) return;
        try
        {
            static double Number(string value) => double.Parse(value, CultureInfo.CurrentCulture);
            var settings = new FilterSettings(SelectedFilter, IsBinarize ? Number(Threshold) : 128,
                IsEdge ? Number(EdgeLow) : 50, IsEdge ? Number(EdgeHigh) : 150);
            settings.Validate();
            editor.SetFilters(settings);
            suppressed = false; Update(editor.State, live); Schedule(force: true);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        { Status = "임계값을 확인하세요. 0–255 숫자와 Edge 하한 < 상한이 필요합니다."; }
    }
    private void RefreshCommands() { Preview?.Refresh(); Stop?.Refresh(); ClearRegistration?.Refresh(); }
    private void ClearResult() { result = null; Changed?.Invoke(); }
    public void StopPreview()
    {
        suppressed = true;
        IsActive = false; revision++; pending = requested = null; cancellation?.Cancel();
        ClearResult(); Status = "원본 표시 · 등록 설정 유지 · 다시 선택하면 자동 미리보기"; RefreshCommands();
    }
    private void Schedule(bool force = false, bool keepVisible = false)
    {
        if (disposed) return;
        var rois = document.SelectionFacts.RegisteredFilters;
        IsActive = !suppressed && document.Image is not null && rois.Length > 0;
        if (!IsActive)
        {
            revision++; pending = requested = null; cancellation?.Cancel(); ClearResult();
            Status = suppressed ? "원본 표시 · 등록 설정 유지 · 다시 선택하면 자동 미리보기" : "등록된 필터 없음 · 필터 선택 후 미리보기로 등록하세요."; return;
        }
        try
        {
            var next = new FilterRequest(document.Image!, live, rois, translation);
            if (!force && requested is { } old && old.Image == next.Image && old.IsLive == next.IsLive &&
                old.Translation == next.Translation && old.Rois.SequenceEqual(next.Rois)) return;
            pending = requested = next; revision++; cancellation?.Cancel();
            if (!keepVisible) { ClearResult(); Status = "선택 영역을 처리하고 있습니다…"; }
            if (pump is null || pump.IsCompleted) pump = RunAsync();
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        { revision++; pending = requested = null; cancellation?.Cancel(); ClearResult(); Status = "임계값을 확인하세요. 0–255 숫자를 입력하세요."; }
    }
    private async Task RunAsync()
    {
        IsBusy = true;
        try
        {
            while (pending is { } request && !disposed)
            {
                pending = null; var version = revision;
                using var current = new CancellationTokenSource(); cancellation = current;
                try
                {
                    await Task.Delay(100, current.Token); // Coalesce rapid selection/property/drag changes.
                    var output = await processor.ProcessAsync(request, current.Token);
                    if (disposed || version != revision || !IsActive) continue;
                    result = output;
                    Status = $"미리보기 켜짐 · {output.Regions.Count}개 ROI · {output.Milliseconds:F0} ms";
                    Changed?.Invoke();
                }
                catch (OperationCanceledException) when (current.IsCancellationRequested) { }
                catch (Exception ex) { if (version == revision && !disposed) { ClearResult(); Status = $"미리보기 실패: {ex.Message}"; } }
                finally { if (ReferenceEquals(cancellation, current)) cancellation = null; }
            }
        }
        finally { IsBusy = false; }
    }
    public void Dispose() { disposed = true; revision++; pending = requested = null; cancellation?.Cancel(); result = null; }
}
