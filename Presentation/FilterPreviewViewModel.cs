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
    private DocumentState document = DocumentState.Empty;
    private bool live, active, busy, disposed;
    private RoiFilterKind selectedFilter;
    private string threshold = "128", edgeLow = "50", edgeHigh = "150", status = "필터를 선택한 뒤 미리보기를 누르세요.";
    private FilterRequest? pending, requested;
    private CancellationTokenSource? cancellation;
    private long revision;
    private Task? pump;
    private FilterPreviewResult? result;
    public IReadOnlyList<FilterOption> Options { get; } = [new(RoiFilterKind.None, "없음 · 원본"), new(RoiFilterKind.Binarize, "Binarize · 이진화"), new(RoiFilterKind.Edge, "Edge · Canny")];
    public RoiFilterKind SelectedFilter { get => selectedFilter; set { if (Set(ref selectedFilter, value)) { Notify(nameof(IsBinarize)); Notify(nameof(IsEdge)); if (value == RoiFilterKind.None) StopPreview(); else Schedule(); RefreshCommands(); } } }
    public string Threshold { get => threshold; set { if (Set(ref threshold, value)) Schedule(); } }
    public string EdgeLow { get => edgeLow; set { if (Set(ref edgeLow, value)) Schedule(); } }
    public string EdgeHigh { get => edgeHigh; set { if (Set(ref edgeHigh, value)) Schedule(); } }
    public bool IsBinarize => SelectedFilter == RoiFilterKind.Binarize;
    public bool IsEdge => SelectedFilter == RoiFilterKind.Edge;
    public bool IsActive { get => active; private set => Set(ref active, value); }
    public bool IsBusy { get => busy; private set => Set(ref busy, value); }
    public string Status { get => status; private set => Set(ref status, value); }
    public FilterPreviewResult? Result => result;
    public event Action? Changed;
    public RelayCommand Preview { get; }
    public RelayCommand Stop { get; }
    public Task WhenIdle => pump ?? Task.CompletedTask;
    public void RefreshLiveFrame() { if (IsActive && live && !IsBusy) Schedule(force: true, keepVisible: true); }

    public FilterPreviewViewModel(IRoiFilterPreview processor)
    {
        this.processor = processor;
        Preview = new(() => { IsActive = true; Schedule(force: true); RefreshCommands(); }, () => SelectedFilter != RoiFilterKind.None && document.Selection.Count > 0);
        Stop = new(StopPreview, () => IsActive);
    }
    public void Update(DocumentState state, bool isLive)
    {
        if (document.Image != state.Image || live != isLive) StopPreview();
        document = state; live = isLive;
        Schedule(); RefreshCommands();
    }
    private void RefreshCommands() { Preview?.Refresh(); Stop?.Refresh(); }
    private void ClearResult() { result = null; Changed?.Invoke(); }
    public void StopPreview()
    {
        IsActive = false; revision++; pending = requested = null; cancellation?.Cancel();
        ClearResult(); Status = "원본 표시 · 필터를 선택한 뒤 미리보기를 누르세요."; RefreshCommands();
    }
    private void Schedule(bool force = false, bool keepVisible = false)
    {
        if (!IsActive || disposed) return;
        if (document.Image is null || document.Selection.Count == 0)
        {
            revision++; pending = requested = null; cancellation?.Cancel(); ClearResult();
            Status = "미리보기 켜짐 · ROI를 선택하세요."; return;
        }
        try
        {
            static double Number(string value) => double.Parse(value, CultureInfo.CurrentCulture);
            var settings = new FilterSettings(SelectedFilter, IsBinarize ? Number(Threshold) : 128,
                IsEdge ? Number(EdgeLow) : 50, IsEdge ? Number(EdgeHigh) : 150);
            settings.Validate();
            var rois = document.Selection.Select(id => document.Rois[id]).OrderBy(r => r.Order).ToArray();
            var next = new FilterRequest(document.Image, live, rois, settings);
            if (!force && requested is { } old && old.Image == next.Image && old.IsLive == next.IsLive && old.Settings == next.Settings && old.Rois.SequenceEqual(next.Rois)) return;
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
