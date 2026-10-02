using System.Windows.Input;
using Microsoft.Win32;
using TeachingToolWpf.Core.Editing;
using TeachingToolWpf.Core.Geometry;
using TeachingToolWpf.Core.Interaction;
using TeachingToolWpf.Imaging;
using TeachingToolWpf.Core.Imaging;

namespace TeachingToolWpf.Presentation;

/// <summary>Presentation state and command availability; editing rules remain in the session.</summary>
public sealed class EditorViewModel : ObservableObject, IDisposable
{
    private readonly IViewportSurface viewport;
    private readonly List<RelayCommand> syncCommands = [];
    private readonly List<AsyncCommand> asyncCommands = [];
    private readonly System.Windows.Threading.DispatcherTimer filterTimer;
    private string status = "이미지를 준비하고 있습니다…", gpuStatus = "DX12 초기화 중…", zoomText = "100";
    private bool pointSampling;
    private DocumentState? previous;
    private IReadOnlyList<RoiRow> rows = [];
    public EditorSession Session { get; } = new();
    public InteractionController Input { get; }
    public ImageCoordinator Images { get; }
    public LivePreview Live { get; }
    public FilterPreviewViewModel Filters { get; }
    public IReadOnlyList<PixelKind> LiveFormats { get; } = [PixelKind.Gray8, PixelKind.Gray16, PixelKind.Rgba32, PixelKind.Bgra32];
    public PixelKind LiveFormat { get; set; } = PixelKind.Gray8;
    public AsyncCommand StartLive { get; }
    public AsyncCommand StopLive { get; }
    public RoiPropertiesViewModel Properties { get; }
    public IReadOnlyList<RoiRow> Rows { get => rows; private set => Set(ref rows, value); }
    public string Status { get => status; set => Set(ref status, value); }
    public string GpuStatus { get => gpuStatus; set => Set(ref gpuStatus, value); }
    public string ZoomText { get => zoomText; set => Set(ref zoomText, value); }
    private ImageInfo? DisplayImage => Live.Image ?? Images.Image;
    public string ImageTitle => DisplayImage?.Name ?? "이미지 없음";
    public string ImageDetails => DisplayImage is { } image ? $"{image.Width:N0} × {image.Height:N0} px   ·   {image.Pixels}" : "이미지를 열어 주세요";
    public string SelectionSummary => $"{Session.State.Selection.Count}개 선택 / ROI {Session.State.Rois.Count}개";
    public int RoiCount => Session.State.Rois.Count;
    public bool HasRois => RoiCount > 0;
    public bool IsLiveRunning => Live.IsRunning;
    public string SelectionTitle => Session.State.Selection.Count switch { 0 => "선택한 영역 없음", 1 => "영역 속성", _ => "여러 영역 선택" };
    public string ToolHint => Input.Tool switch
    {
        EditorTool.Draw => "ROI를 그리면 선택 도구로 돌아갑니다. 다시 그리기: R · 취소: Esc",
        EditorTool.Pan => "드래그해 화면을 이동하세요. 휠로 확대·축소합니다.",
        _ => "클릭으로 선택 · Ctrl로 다중 선택 · 휠로 확대·축소"
    };
    public string ToolName => Input.Tool switch { EditorTool.Pan => "이동", EditorTool.Draw => "ROI 그리기", _ => "선택" };
    public bool IsSelect => Input.Tool == EditorTool.Select;
    public bool IsPan => Input.Tool == EditorTool.Pan;
    public bool IsDraw => Input.Tool == EditorTool.Draw;
    public bool CanEdit => !Images.IsBusy;
    public bool PointSampling { get => pointSampling; set { if (Set(ref pointSampling, value)) Publish(); } }
    public string UndoHint => Session.CanUndo ? $"실행 취소: {Session.UndoName} (Ctrl+Z)" : "취소할 편집이 없습니다.";
    public string RedoHint => Session.CanRedo ? $"다시 실행: {Session.RedoName} (Ctrl+Y)" : "다시 실행할 편집이 없습니다.";
    public RelayCommand SelectTool { get; }
    public RelayCommand PanTool { get; }
    public RelayCommand DrawTool { get; }
    public RelayCommand AddRoi { get; }
    public RelayCommand ZoomIn { get; }
    public RelayCommand ZoomOut { get; }
    public RelayCommand ApplyZoom { get; }
    public RelayCommand Fit { get; }
    public RelayCommand ActualSize { get; }
    public RelayCommand Left { get; }
    public RelayCommand Right { get; }
    public RelayCommand Up { get; }
    public RelayCommand Down { get; }
    public RelayCommand Delete { get; }
    public RelayCommand Group { get; }
    public RelayCommand Ungroup { get; }
    public RelayCommand ResetAngle { get; }
    public RelayCommand RotateLeft { get; }
    public RelayCommand RotateRight { get; }
    public RelayCommand ApplyProperties { get; }
    public RelayCommand SelectAll { get; }
    public RelayCommand CancelImport { get; }
    public AsyncCommand OpenImage { get; }
    public AsyncCommand Sample8K { get; }
    public AsyncCommand Sample24K { get; }
    public AsyncCommand Undo { get; }
    public AsyncCommand Redo { get; }
    public AsyncCommand RestartRenderer { get; }
    private string performance = "";
    public string Performance { get => performance; set => Set(ref performance, value); }

    public EditorViewModel(IViewportSurface viewport, IImageLoader loader, LivePreview live, Imaging.Filters.IRoiFilterPreview filterPreview)
    {
        this.viewport = viewport;
        Live = live;
        Filters = new(filterPreview);
        Filters.Changed += Publish;
        filterTimer = new(TimeSpan.FromMilliseconds(200), System.Windows.Threading.DispatcherPriority.Background,
            (_, _) => { if (Live.IsRunning) Filters.RefreshLiveFrame(); }, System.Windows.Threading.Dispatcher.CurrentDispatcher);
        Input = new(Session); Images = new(Session, viewport, loader, Live.CloseAsync); Properties = new(Session);
        SelectTool = Command(() => SetTool(EditorTool.Select)); PanTool = Command(() => SetTool(EditorTool.Pan)); DrawTool = Command(() => SetTool(EditorTool.Draw), HasImage);
        AddRoi = Command(AddRoiInView, HasImage);
        ZoomIn = Command(() => Zoom(1.25), HasImage); ZoomOut = Command(() => Zoom(.8), HasImage);
        ApplyZoom = Command(SetZoom, HasImage); Fit = Command(FitImage, HasImage); ActualSize = Command(() => ZoomTo(1 / viewport.Dpi), HasImage);
        Left = Command(() => Pan(100, 0)); Right = Command(() => Pan(-100, 0)); Up = Command(() => Pan(0, 100)); Down = Command(() => Pan(0, -100));
        Delete = Command(Session.DeleteSelection, HasSelection);
        Group = Command(() =>
        {
            var before = Session.State.Selection.Count;
            var id = Session.GroupSelection();
            var count = RoiHierarchy.Subtree(Session.State, [id]).Count - 1;
            Status = $"새 부모를 만들었습니다. 포함 ROI {count}개 · 선택 {before}개";
        }, () => RoiHierarchy.EffectiveRoots(Session.State).Count() >= 2);
        Ungroup = Command(Session.UngroupSelection, () => Session.State.Selection.Any(id => Session.State.Rois[id].IsGroup));
        ResetAngle = Command(Session.ResetAngles, HasSelection);
        RotateLeft = Command(() => Rotate(-15), HasSelection); RotateRight = Command(() => Rotate(15), HasSelection);
        ApplyProperties = Command(Properties.Apply, () => Properties.HasSingleSelection);
        SelectAll = Command(() => Session.Select(Session.State.Rois.Keys), () => Session.State.Rois.Count != 0);
        CancelImport = new(Images.Cancel, () => Images.IsBusy);
        OpenImage = Async(OpenAsync); Sample8K = Async(() => LoadSampleAsync(8192, 8192)); Sample24K = Async(() => LoadSampleAsync(24576, 32768));
        Undo = Async(() => HistoryAsync(false), () => Session.CanUndo); Redo = Async(() => HistoryAsync(true), () => Session.CanRedo);
        StartLive = Async(StartLiveAsync, () => !Live.IsRunning);
        StopLive = Async(Live.StopAsync, () => Live.IsRunning);
        RestartRenderer = Async(viewport.RestartAsync);
        Live.Changed += Refresh;
        Session.Changed += SessionChanged;
        Input.Changed += InputChanged;
        Images.Changed += Refresh;
        Images.Status += text => Status = text;
        viewport.LayoutChanged += Publish;
    }
    public Task InitializeAsync() => LoadSampleAsync(8192, 8192);
    private async Task HistoryAsync(bool redo)
    {
        var target = redo ? Session.PreviewRedo() : Session.PreviewUndo();
        var sourceChanged = target.Image != Session.State.Image;
        await Images.HistoryAsync(redo);
        if (sourceChanged && Session.State.Image == target.Image && LiveImageSource.TryKind(target.Image, out var kind)) Live.Start(kind);
    }
    private async Task StartLiveAsync()
    {
        var reference = Live.Reference(LiveFormat);
        await Images.ReplaceAsync(reference);
        if (Session.State.Image?.Key != reference.Key) return;
        Live.Start(LiveFormat); FitImage();
        Status = "8K 합성 프레임 재생 · 실제 카메라 수집은 IFrameSource로 연결";
    }
    private bool HasImage() => DisplayImage is not null;
    private bool HasSelection() => Session.State.Selection.Count > 0;
    private RelayCommand Command(Action action, Func<bool>? available = null)
    {
        var command = new RelayCommand(() => Guard(action), () => CanEdit && !Input.IsDragging && (available?.Invoke() ?? true)); syncCommands.Add(command); return command;
    }
    private AsyncCommand Async(Func<Task> action, Func<bool>? available = null)
    {
        var command = new AsyncCommand(action, Fail, () => CanEdit && !Input.IsDragging && (available?.Invoke() ?? true)); asyncCommands.Add(command); return command;
    }
    public void Guard(Action action) { try { action(); } catch (Exception ex) { Fail(ex); } }
    public void Fail(Exception ex) => Status = ex.Message;
    private void SetTool(EditorTool tool) { Input.Tool = tool; Status = tool == EditorTool.Draw ? "ROI 한 개를 그리면 선택 도구로 돌아갑니다. 다시 그리기: R · 취소: Esc" : tool == EditorTool.Pan ? "드래그로 화면을 이동합니다. 휠: 확대/축소" : "Ctrl+클릭: 다중 선택 · 빈 곳 드래그: 범위 선택 · Shift: 이동 축/회전 15° 고정"; }
    private void InputChanged() { Notify(nameof(ToolName)); Notify(nameof(ToolHint)); Notify(nameof(IsSelect)); Notify(nameof(IsPan)); Notify(nameof(IsDraw)); RefreshCommands(); Publish(); }
    private void SessionChanged(object? sender, EventArgs e)
    {
        var state = Session.State;
        if (!Session.IsPreviewing && (previous is null || previous.Rois.Count != state.Rois.Count || state.Rois.Values.Any(r => !previous.Rois.TryGetValue(r.Id, out var old) || old.ParentId != r.ParentId || old.Name != r.Name))) Rows = RoiRow.Build(state);
        previous = state;
        Properties.Refresh(); ZoomText = (state.Viewport.Scale * viewport.Dpi * 100).ToString("0.##");
        Refresh();
    }
    private void Refresh()
    {
        Filters.Update(Session.State, Live.Image is not null);
        Notify(nameof(CanEdit)); Notify(nameof(SelectionSummary)); Notify(nameof(ImageTitle)); Notify(nameof(ImageDetails)); Notify(nameof(UndoHint)); Notify(nameof(RedoHint));
        Notify(nameof(RoiCount)); Notify(nameof(HasRois)); Notify(nameof(SelectionTitle)); Notify(nameof(IsLiveRunning));
        RefreshCommands(); Publish();
    }
    private void RefreshCommands() { foreach (var command in syncCommands) command.Refresh(); foreach (var command in asyncCommands) command.Refresh(); CancelImport.Refresh(); }
    private PointD Center => new(viewport.ViewWidth / 2, viewport.ViewHeight / 2);
    private void AddRoiInView()
    {
        if (DisplayImage is not { } image) return;
        var geometry = RoiPlacement.InView(image.Width, image.Height, Session.State.Viewport, viewport.ViewWidth, viewport.ViewHeight);
        var screen = Session.State.Viewport.ImageToScreen(geometry.Center);
        if (screen.X < 0 || screen.Y < 0 || screen.X > viewport.ViewWidth || screen.Y > viewport.ViewHeight) FitImage();
        Session.Add(geometry);
        SetTool(EditorTool.Select);
        Status = "ROI를 추가했습니다. 핸들로 크기와 회전을 조절하세요.";
    }
    private void Pan(double x, double y) => Session.SetViewport(Session.State.Viewport.Pan(new(x, y)));
    private void Zoom(double factor) => Session.SetViewport(Session.State.Viewport.ZoomAt(Center, factor));
    private void ZoomTo(double scale) => Zoom(scale / Session.State.Viewport.Scale);
    private void SetZoom()
    {
        if (!double.TryParse(ZoomText.TrimEnd('%'), out var percent) || !double.IsFinite(percent) || percent <= 0) throw new ArgumentException("배율에는 0보다 큰 숫자를 입력하세요.");
        ZoomTo(percent / (100 * viewport.Dpi));
    }
    private void FitImage() { if (DisplayImage is { } image) Session.SetViewport(ViewportTransform.Fit(image.Width, image.Height, viewport.ViewWidth, viewport.ViewHeight)); }
    private void Rotate(double angle)
    {
        var bounds = BoundsD.FromPoints(Session.State.Selection.SelectMany(id => Session.State.Rois[id].Geometry.Corners));
        Session.RotateSelection(angle, bounds.Center);
    }
    private async Task OpenAsync()
    {
        var dialog = new OpenFileDialog { Title = "배경 이미지 열기 · 대용량 이미지는 최초 캐시 생성이 필요합니다", Filter = "이미지|*.png;*.jpg;*.jpeg;*.tif;*.tiff;*.webp;*.v|모든 파일|*.*" };
        if (dialog.ShowDialog() == true) await Images.ReplaceAsync(new(dialog.FileName, "", 0, 0, DiskPyramidSource.Version(dialog.FileName)));
    }
    private Task LoadSampleAsync(int width, int height) => Images.ReplaceAsync(new($"sample:{width}:{height}", "", width, height));
    public void Publish() => viewport.Submit(new(Session.State, DisplayImage, Images.Generation, viewport.ViewWidth, viewport.ViewHeight, viewport.Dpi, Input.Overlay, PointSampling, Live.Image is not null, Filters.Result));
    public void SelectRows(IEnumerable<Guid> ids) { if (CanEdit && !Input.IsDragging) Session.Select(ids); }
    public bool HandleKey(Key key, bool control, bool shift)
    {
        if (key == Key.Escape) { Input.Cancel(); Images.Cancel(); return true; }
        ICommand? command = (control, shift, key) switch
        {
            (true, false, Key.Z) => Undo, (true, _, Key.Y) or (true, true, Key.Z) => Redo,
            (true, true, Key.G) => Ungroup, (true, false, Key.G) => Group, (true, _, Key.A) => SelectAll,
            (true, _, Key.O) => OpenImage, (false, _, Key.Delete) => Delete,
            (false, _, Key.V) => SelectTool, (false, _, Key.H) => PanTool, (false, _, Key.R) => DrawTool,
            (false, _, Key.F) => Fit, (false, _, Key.D0) => ResetAngle, _ => null
        };
        if (command is not null) { if (command.CanExecute(null)) command.Execute(null); return true; }
        if (CanEdit && !Input.IsDragging && HasSelection() && key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            var step = shift ? 10 : 1;
            Guard(() => Session.TranslateSelection(key switch { Key.Left => new(-step, 0), Key.Right => new(step, 0), Key.Up => new(0, -step), _ => new(0, step) })); return true;
        }
        return false;
    }
    public void Dispose()
    {
        filterTimer.Stop(); Filters.Changed -= Publish; Filters.Dispose();
        Live.Dispose(); Live.Changed -= Refresh; Images.Dispose(); viewport.LayoutChanged -= Publish; Session.Changed -= SessionChanged; Input.Changed -= InputChanged; Images.Changed -= Refresh;
    }
}
