using System.IO;
using System.Windows.Input;
using TeachingToolWpf.Commands;
using TeachingToolWpf.Infrastructure;
using TeachingToolWpf.Models;
using TeachingToolWpf.Services;
using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Geometry;
using VisionCanvas.Core.Interaction;
using VisionCanvas.Diagnostics;
using VisionCanvas.Presentation;
using VisionCanvas.Rendering;
using VisionGeometry;
using VisionImaging;
using VisionImaging.Abstractions;
using FilterPreviewViewModel = VisionCanvas.Filters.Wpf.ViewModels.FilterPreviewViewModel;

namespace TeachingToolWpf.ViewModels;

/// <summary>MainWindow의 화면 상태와 명령. 편집 규칙은 EditorSession이 담당한다.</summary>
public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly Func<string?>? _selectImage;
    public ViewportLayout Layout { get; }
    public RenderScene? Scene { get; private set; }
    public Func<Key, bool, bool, bool> KeyHandler => HandleKey;
    public ViewportNavigationViewModel Navigation { get; }
    private readonly List<RelayCommand> _syncCommands = [];
    private readonly List<AsyncCommand> _asyncCommands = [];
    private readonly RoiClipboard _clipboard = new();
    private readonly RoiPreviewPreparation _previewPreparation;
    private CancellationTokenSource? _editCancellation;
    private bool _disposed;

    public Task PasteCompletion { get; private set; } = Task.CompletedTask;

    private string _status = "이미지를 준비하고 있습니다…";

    private string _gpuStatus = "DX12 초기화 중…";

    private string _zoomText = "100";
    private bool _pointSampling;
    private object? _rowVersion;
    private System.Collections.Immutable.ImmutableDictionary<Guid, Roi>? _rowRois;
    private RoiRows _rows = new([]);
    // Small documents keep the virtualized list stable; paging bounds WPF's unresolved multi-selection scan at scale.
    private int RowPageSize => Rows.Count <= 4096 ? 4096 : 512;

    private int _rowPage;
    private RoiRows _visibleRows = new([]);
    private object? _rowSelection;

    public RoiRows VisibleRows => _visibleRows;

    public string RowPageLabel => $"{_rowPage + 1:N0} / {Math.Max(1, (Rows.Count + RowPageSize - 1) / RowPageSize):N0} 페이지";

    public event Action? RowPageChanged;

    public RelayCommand PreviousRowPage { get; private set; }

    public RelayCommand NextRowPage { get; private set; }

    public RoiRow? FindRow(Guid id) => Rows.Find(id);

    public EditorSession Session { get; } = new();

    public InteractionController Input { get; }

    public RoiMaskViewModel Masks { get; }

    public ImageCoordinator Images { get; }

    public LivePreview Live { get; }

    public FilterPreviewViewModel Filters { get; }

    public ShapeMatchingViewModel Matching { get; }

    public InspectionToolsViewModel Inspections { get; }

    public IReadOnlyList<PixelKind> LiveFormats { get; } = [PixelKind.Gray8, PixelKind.Gray16, PixelKind.Rgba32, PixelKind.Bgra32];

    public PixelKind LiveFormat { get; set; } = PixelKind.Gray8;

    public AsyncCommand StartLive { get; private set; }

    public AsyncCommand StopLive { get; private set; }

    public RoiPropertiesViewModel Properties { get; }

    public RoiStyleSettingsViewModel StyleSettings { get; } = new();

    public CrosshairSettingsViewModel GuideSettings { get; } = new();

    public RoiRows Rows
    {
        get => _rows;
        private set => Set(ref _rows, value);
    }

    public string Status
    {
        get => _status;
        set => Set(ref _status, value);
    }

    public string GpuStatus
    {
        get => _gpuStatus;
        set => Set(ref _gpuStatus, value);
    }

    public string ZoomText
    {
        get => _zoomText;
        set => Set(ref _zoomText, value);
    }

    private ImageInfo? DisplayImage => Live.Image ?? Images.Image;

    public string ImageTitle => DisplayImage?.Name ?? "이미지 없음";

    public string ImageDetails => DisplayImage is { } image ? $"{image.Width:N0} × {image.Height:N0} px   ·   {image.Pixels}" : "이미지를 열어 주세요";

    public string SelectionSummary => $"{Session.State.SelectionCount:N0}개 선택 / ROI {Session.State.RoiCount:N0}개";

    public long RoiCount => Session.State.RoiCount;

    public bool HasRois => RoiCount > 0;

    public bool IsLiveRunning => Live.IsRunning;

    public string SelectionTitle => Session.State.SelectionCount switch { 0 => "선택한 영역 없음", 1 => "영역 속성", _ => "여러 영역 선택" };
    public bool HasRepeatedSelection => false;
    public bool CanUseOrdinaryRoiTools => true;

    public string ToolHint => Input.Tool switch
    {
        EditorTool.Mask => "마스크 그리기 · ROI 안에서 드래그 · 완료/Esc로 종료 · 가운데 버튼으로 이동",
        EditorTool.Draw when Input.DrawShape == RoiShapeKind.Polygon => $"꼭짓점 {Input.PolygonPointCount}개 · 클릭으로 연결 · 교차점에서 자동 완성 · 완료/Enter/첫 점 · 취소: Esc",
        EditorTool.Draw when Input.DrawAsGroup => "부모 ROI 그리기 · 완성 후 부모 선택 → ROI 추가/그리기 · 취소: Esc",
        EditorTool.Draw => $"{(Input.DrawParentId is null ? "부모 없이" : "선택 부모의 자식")} ROI 그리기 · 완료 후 선택 도구 복귀 · 취소: Esc",
        EditorTool.Pan => "드래그해 화면을 이동하세요. 휠로 확대·축소합니다.",
        _ => "더블클릭: 겹친 ROI/그룹 순환 · 선택 그룹 내부 드래그 · Ctrl: 다중 선택 · 휠: 확대·축소"
    };

    public string ToolName => Input.Tool switch
    {
        EditorTool.Mask => "마스크 그리기",
        EditorTool.Pan => "이동",
        EditorTool.Draw when Input.DrawAsGroup => "부모 ROI 그리기",
        EditorTool.Draw => "ROI 그리기",
        _ => "선택"
    };

    public bool IsSelect => Input.Tool == EditorTool.Select;

    public bool IsPan => Input.Tool == EditorTool.Pan;

    public bool IsDraw => Input.Tool == EditorTool.Draw;

    public IReadOnlyList<ShapeOption> ShapeOptions { get; } = [new(RoiShapeKind.Rectangle, "사각형"), new(RoiShapeKind.Circle, "원"), new(RoiShapeKind.Triangle, "삼각형"), new(RoiShapeKind.Polygon, "폴리곤")];

    public RoiShapeKind SelectedShape
    {
        get => Input.DrawShape;
        set
        {
            Input.DrawShape = value;
            Notify();
        }
    }

    public RelayCommand CompletePolygon { get; private set; }

    public RelayCommand RemovePolygonPoint { get; private set; }

    public RelayCommand CancelPolygon { get; private set; }

    public bool CanEdit => CanNavigate && !Images.IsBusy && _editCancellation is null;

    public bool CanNavigate => !_disposed && Input.IsEnabled;

    public bool PointSampling
    {
        get => _pointSampling;
        set
        {
            if (Set(ref _pointSampling, value))
            {
                Publish();
            }
        }
    }

    public bool ShowMinimap
    {
        get => Input.MinimapOptions.IsVisible;
        set
        {
            if (ShowMinimap == value)
            {
                return;
            }
            Input.MinimapOptions = Input.MinimapOptions with
            {
                IsVisible = value
            };
            Notify();
        }
    }

    public MinimapPosition MinimapPosition
    {
        get => Input.MinimapOptions.Position;
        set
        {
            if (MinimapPosition == value)
            {
                return;
            }
            Input.MinimapOptions = Input.MinimapOptions with
            {
                Position = value
            };
            Notify();
        }
    }

    public string UndoHint => Session.CanUndo ? $"실행 취소: {Session.UndoName} (Ctrl+Z)" : "취소할 편집이 없습니다.";

    public string RedoHint => Session.CanRedo ? $"다시 실행: {Session.RedoName} (Ctrl+Y)" : "다시 실행할 편집이 없습니다.";

    public RelayCommand SelectTool { get; private set; }

    public RelayCommand PanTool { get; private set; }

    public RelayCommand DrawTool { get; private set; }

    public RelayCommand AddRoi { get; private set; }

    public RelayCommand ZoomIn { get; private set; }

    public RelayCommand ZoomOut { get; private set; }

    public RelayCommand ApplyZoom { get; private set; }

    public RelayCommand Fit { get; private set; }

    public RelayCommand ActualSize { get; private set; }

    public RelayCommand Left { get; private set; }

    public RelayCommand Right { get; private set; }

    public RelayCommand Up { get; private set; }

    public RelayCommand Down { get; private set; }

    public AsyncCommand Delete { get; private set; }

    public RelayCommand Copy { get; private set; }

    public AsyncCommand Paste { get; private set; }

    public AsyncCommand Group { get; private set; }

    public AsyncCommand Ungroup { get; private set; }

    public RelayCommand ResetAngle { get; private set; }

    public RelayCommand RotateLeft { get; private set; }

    public RelayCommand RotateRight { get; private set; }

    public RelayCommand ApplyProperties { get; private set; }

    public AsyncCommand SelectAll { get; private set; }

    public RelayCommand CancelImport { get; private set; }

    public AsyncCommand Initialize { get; private set; }

    public AsyncCommand OpenImage { get; private set; }

    public AsyncCommand Sample8K { get; private set; }

    public AsyncCommand Sample24K { get; private set; }

    public AsyncCommand PatternSample { get; private set; }

    public AsyncCommand PrimitiveSample { get; private set; }

    public AsyncCommand Undo { get; private set; }

    public AsyncCommand Redo { get; private set; }

    public AsyncCommand RestartRenderer { get; private set; }

    private string _performance = "";

    public string Performance
    {
        get => _performance;
        set => Set(ref _performance, value);
    }

    public MainWindowViewModel(IImageDisplay display, ViewportLayout layout, IImageLoader loader, LivePreview live, VisionCanvas.Imaging.Filters.IRoiFilterPreview filterPreview,
        InspectionToolsViewModel inspections, Func<string?>? selectImage = null)
        : this(display, layout, loader, live, filterPreview, inspections, null, selectImage)
    {
    }

    internal MainWindowViewModel(IImageDisplay display, ViewportLayout layout, IImageLoader loader, LivePreview live, VisionCanvas.Imaging.Filters.IRoiFilterPreview filterPreview,
        InspectionToolsViewModel inspections, Action<DocumentState, CancellationToken>? preparePreview, Func<string?>? selectImage = null)
    {
        Layout = layout;
        _selectImage = selectImage;
        StyleSettings.Changed += ApplyRoiSettings;
        Live = live;
        Filters = new(filterPreview, Session);
        Filters.Changed += Publish;
        _previewPreparation = new(Session, ReportError, () => Status = "선택 영역 준비 중… 최신 포인터 위치 반영", preparePreview);
        Input = new(Session, CompletePreview, CompleteSelection, _previewPreparation.SubmitTranslation, _previewPreparation.SubmitRotation, CompleteRegionSelection);
        Navigation = new(Session, Input, Layout, () => CanEdit);
        Masks = new(Session, Input, message => Status = message);
        Masks.Changed += Publish;
        Images = new(Session, display, layout, loader, Live.CloseAsync, PrepareRowsAsync);
        Inspections = inspections;
        Matching = inspections.Pattern;
        Inspections.Changed += Publish;
        Matching.LocateRequested += LocateInspection;
        Matching.SearchRangeRequested += EnsurePatternSearchRange;
        Inspections.Shapes.LocateRequested += LocateInspection;
        Properties = new(Session);
        WireCommands(display);
        Live.Changed += Refresh;
        Session.Changed += SessionChanged;
        Input.Changed += InputChanged;
        Images.Changed += Refresh;
        Images.Status += text => Status = text;
        Layout.Changed += LayoutChanged;
    }

    public void UpdatePerformance(RenderMetrics metrics)
    {
        Performance = metrics.GpuMilliseconds is { } gpuMilliseconds
            ? $"CPU {metrics.CpuMilliseconds:F1} ms · GPU {gpuMilliseconds:F1} ms · 표시 드롭 {metrics.Dropped}"
            : $"CPU 제출 {metrics.CpuMilliseconds:F1} ms · 장치 시간 미측정 · 표시 드롭 {metrics.Dropped}";
    }

    public void RefreshLivePreview()
    {
        if (!_disposed && Live.IsRunning)
        {
            Filters.RefreshLiveFrame();
        }
    }

    private void LayoutChanged()
    {
        ZoomText = (Session.State.Viewport.Scale * Layout.Dpi * 100).ToString("0.##");
        Publish();
    }

    public Task InitializeAsync() => LoadSampleAsync(8192, 8192);

    private void CopySelection()
    {
        var copiedCount = _clipboard.Copy(Session.State);
        Status = $"ROI {copiedCount}개 복사 · Ctrl+V로 붙여넣기";
        RefreshCommands();
    }

    private async Task PasteAsync()
    {
        using var cancellation = new CancellationTokenSource();
        _editCancellation = cancellation;
        Status = $"ROI {_clipboard.Count:N0}개 붙여넣기 준비 중… Esc로 취소";
        Refresh();
        try
        {
            if (_clipboard.Count <= 256)
            {
                Session.Paste(_clipboard);
            }
            else
            {
                var prepared = await Session.PreparePasteAsync(_clipboard, cancellation.Token);
                await PrepareRowsAsync(prepared.State, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                Session.CommitPreparedPaste(prepared);
            }
            SetTool(EditorTool.Select);
            Status = "ROI를 붙여넣었습니다. Ctrl+Z로 한 번에 취소할 수 있습니다.";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Status = "붙여넣기를 취소했습니다.";
        }
        finally
        {
            _preparedRows = null;
            _editCancellation = null;
            if (!_disposed)
            {
                Refresh();
            }
        }
    }

    private async Task HistoryAsync(bool redo)
    {
        var beforeImage = Session.State.Image;
        try
        {
            await Images.HistoryAsync(redo);
            if (!_disposed && Session.State.Image != beforeImage && LiveImageSource.TryKind(Session.State.Image, out var kind))
            {
                Live.Start(kind);
            }
        }
        finally
        {
            _preparedRows = null;
        }
    }

    private async Task StartLiveAsync()
    {
        var reference = Live.Reference(LiveFormat);
        await Images.ReplaceAsync(reference);
        if (Session.State.Image?.Key != reference.Key)
        {
            return;
        }

        Live.Start(LiveFormat);
        FitImage();
        Status = "8K 합성 프레임 재생 · 실제 카메라 수집은 IFrameSource로 연결";
    }

    private bool HasImage() => DisplayImage is not null;

    private bool HasSelection() => Session.State.SelectionCount > 0;

    /// <summary>명령 하나를 실행하고, 예외는 상태 표시줄 메시지로 바꾼다.</summary>
    /// <remarks>명령은 사용자 입력에서 시작하므로 예외가 UI 스레드 밖으로 새면 앱이 죽는다. 여기서 마지막으로 막는다.</remarks>
    public void RunGuarded(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            ReportError(ex);
        }
    }

    /// <summary>예외 메시지를 상태 표시줄에 남긴다. 예외를 다시 던지지 않는다.</summary>
    public void ReportError(Exception ex) => Status = ex.Message;

    private void SetTool(EditorTool tool)
    {
        Input.Tool = tool;
        Status = tool switch
        {
            EditorTool.Draw => "ROI 한 개를 그리면 선택 도구로 돌아갑니다. 다시 그리기: R · 취소: Esc",
            EditorTool.Pan => "드래그로 화면을 이동합니다. 휠: 확대/축소",
            _ => "Ctrl+클릭: 다중 선택 · 빈 곳 드래그: 범위 선택 · Shift: 이동 축/회전 15° 고정"
        };
    }

    private void InputChanged()
    {
        if (!Input.IsEnabled)
        {
            _editCancellation?.Cancel();
            _previewPreparation.Cancel();
            Session.CancelPreview();
        }
        Input.NavigationOnly = !CanEdit;
        Navigation.Refresh();
        Notify(nameof(CanEdit));
        Notify(nameof(CanNavigate));
        Masks.Update(CanEdit);
        if (!Session.IsPreviewing)
        {
            _previewPreparation.Cancel();
        }

        Notify(nameof(ToolName));
        Notify(nameof(ToolHint));
        Notify(nameof(IsSelect));
        Notify(nameof(IsPan));
        Notify(nameof(IsDraw));
        RefreshCommands();
        Publish();
    }

    private void EnsurePatternSearchRange(Guid id)
    {
        if (Session.State.Rois.TryGetValue(id, out var roi) && roi.SearchRange is null && !Session.State.IsGroupRoi(id))
        {
            Session.SetProperties(id, roi.Name, roi.Geometry, RoiSearchRange.Default, roi.IsNameVisible);
        }
    }

    private void SessionChanged(object? sender, EventArgs e)
    {
        Input.ValidateDraftImage();
        if (!Session.IsPreviewing)
        {
            _previewPreparation.Cancel();
        }

        var state = Session.State;
        RefreshRowsForDocument(state);
        RevealSingleSelection(state);
        _rowSelection = state.Selection;
        if (!Session.IsPreviewing)
        {
            _rowRois = state.Rois;
        }

        Properties.Refresh();
        ZoomText = (state.Viewport.Scale * Layout.Dpi * 100).ToString("0.##");
        Refresh();
    }

    private void RefreshRowsForDocument(DocumentState state)
    {
        if (Session.IsPreviewing || ReferenceEquals(_rowVersion, state.Snapshot.RowVersion))
        {
            return;
        }

        if (_preparedRows is { } prepared && ReferenceEquals(prepared.Version, state.Snapshot.RowVersion))
        {
            Rows = prepared.Rows;
        }
        else if (!Rows.TryRefreshMetadata(_rowRois, state))
        {
            Rows = new(RoiRow.Build(state));
        }

        _rowVersion = state.Snapshot.RowVersion;
        var lastPage = Math.Max(0, (Rows.Count - 1) / RowPageSize);
        ChangeRowPage(Math.Min(_rowPage, lastPage), force: true);
    }

    private void RevealSingleSelection(DocumentState state)
    {
        // pan/zoom은 사용자가 열어 둔 목록 페이지를 바꾸지 않는다.
        if (ReferenceEquals(_rowSelection, state.Selection) || state.Selection.Count != 1)
        {
            return;
        }

        var index = Rows.IndexOf(state.Selection.First());
        if (index >= 0)
        {
            ChangeRowPage(index / RowPageSize);
        }
    }

    private void ChangeRowPage(int page, bool force = false)
    {
        if (!force && page == _rowPage)
        {
            return;
        }

        _rowPage = page;
        _visibleRows = Rows.Page(page, RowPageSize);
        Notify(nameof(VisibleRows));
        Notify(nameof(RowPageLabel));
        RowPageChanged?.Invoke();
        RefreshCommands();
    }

    private void Refresh()
    {
        if (_disposed)
        {
            return;
        }
        Navigation.Refresh();
        Input.NavigationOnly = !CanEdit;
        Masks.Update(CanEdit);
        Filters.Update(Session.State, Live.Image is not null, Session.TransformPreview, DisplayImage?.Pixels ?? PixelKind.Gray8);
        Inspections.Update(Session.State, Images.Generation, Live.Image is not null, CanEdit && !Live.IsRunning && !Session.IsPreviewing);
        Notify(nameof(HasRepeatedSelection));
        Notify(nameof(CanUseOrdinaryRoiTools));
        Notify(nameof(CanEdit));
        Notify(nameof(CanNavigate));
        Notify(nameof(SelectionSummary));
        Notify(nameof(ImageTitle));
        Notify(nameof(ImageDetails));
        Notify(nameof(UndoHint));
        Notify(nameof(RedoHint));
        Notify(nameof(RoiCount));
        Notify(nameof(HasRois));
        Notify(nameof(SelectionTitle));
        Notify(nameof(IsLiveRunning));
        RefreshCommands();
        Publish();
    }

    private void RefreshCommands()
    {
        foreach (var command in _syncCommands)
        {
            command.Refresh();
        }

        foreach (var command in _asyncCommands)
        {
            command.Refresh();
        }

        CancelImport.Refresh();
    }

    private PointD Center => new(Layout.Width / 2, Layout.Height / 2);

    private void Pan(double x, double y) => Session.SetViewport(Session.State.Viewport.Pan(new(x, y)));

    private void Zoom(double factor) => Session.SetViewport(Session.State.Viewport.ZoomAt(Center, factor));

    private void ZoomTo(double scale) => Zoom(scale / Session.State.Viewport.Scale);

    private void SetZoom()
    {
        if (!double.TryParse(ZoomText.TrimEnd('%'), out var percent) || !double.IsFinite(percent) || percent <= 0)
        {
            throw new ArgumentException("배율에는 0보다 큰 숫자를 입력하세요.");
        }

        ZoomTo(percent / (100 * Layout.Dpi));
    }

    private void FitImage()
    {
        if (DisplayImage is { } image)
        {
            Session.SetViewport(ViewportTransform.Fit(image.Width, image.Height, Layout.Width, Layout.Height));
        }
    }

    private void LocateInspection(BoundsD bounds)
    {
        if (!CanNavigate || Input.IsDragging)
        {
            return;
        }
        var fitted = ViewportTransform.Fit(Math.Max(1, bounds.Width), Math.Max(1, bounds.Height), Layout.Width, Layout.Height);
        var scale = Math.Min(fitted.Scale, 8 / Layout.Dpi);
        Session.SetViewport(new(scale, Center - (bounds.Center * scale)));
    }

    private void Rotate(double angle)
    {
        Session.BeginPreview();
        _previewPreparation.SubmitRotation(Session.State.SelectedSpatial.Bounds.Center, angle);
        CompletePreview("ROI 회전");
    }

    private async Task OpenAsync()
    {
        if (_selectImage?.Invoke() is { } path)
        {
            await Images.ReplaceAsync(new(path, "", 0, 0, ImageFileVersion.Read(path)));
        }
    }

    private Task LoadSampleAsync(int width, int height) => Images.ReplaceAsync(new($"sample:{width}:{height}", "", width, height));

    private async Task LoadPatternSampleAsync()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "pattern-matching.png");
        await Images.ReplaceAsync(new(path, "", 0, 0, ImageFileVersion.Read(path)));
        Matching.Method = PatternMatchingMethod.Shape;
        Matching.EnableScale = true;
        Matching.UseSelection = false;
    }

    private void ApplyRoiSettings()
    {
        if (Input.HandleScale == StyleSettings.HandleScale)
        {
            Publish();
        }
        else
        {
            // Input.Changed를 통해 입력 범위와 핸들 표시를 한 번에 게시한다.
            Input.HandleScale = StyleSettings.HandleScale;
        }
    }

    public void Publish()
    {
        if (_disposed)
        {
            return;
        }
        Input.SetViewportSize(Layout.Width, Layout.Height);
        Scene = new(Session.State, DisplayImage, Images.Generation, Layout.Width, Layout.Height, Layout.Dpi, Input.Overlay, PointSampling, Live.Image is not null, Filters.Result, Session.TransformPreview, StyleSettings.Palette, Inspections.Overlay)
        {
            ShowMasks = Masks.ShowMasks,
            MinimapOptions = Input.MinimapOptions,
            SelectedMaskIndex = Masks.SelectedIndex,
            MaskEditing = Input.Tool == EditorTool.Mask
        };
        Notify(nameof(Scene));
    }

    public void SelectRows(IEnumerable<Guid> ids)
    {
        if (CanEdit && !Input.IsDragging)
        {
            var captured = ids.ToArray();
            if (captured.Length <= SynchronousPreviewRoiLimit || Session.State.Rois.Count <= SynchronousSelectionDocumentLimit)
            {
                Session.Select(captured);
            }
            else
            {
                EditCompletion = ObserveEditAsync(PrepareEditAsync("목록 선택", token => Session.PrepareSelectionAsync(captured, token)));
            }
        }
    }

    public bool HandleKey(Key key, bool control, bool shift)
    {
        if (!Input.IsEnabled)
        {
            return false;
        }
        if (key == Key.Escape)
        {
            CancelActiveOperations();
            return true;
        }
        if (FindShortcutCommand(key, control, shift) is { } command)
        {
            if (command.CanExecute(null))
            {
                command.Execute(null);
            }
            return true;
        }
        return !control && TryNudgeSelection(key, shift);
    }

    private void CancelActiveOperations()
    {
        Input.Cancel();
        if (Input.Tool == EditorTool.Mask)
        {
            Input.Tool = EditorTool.Select;
        }
        // Mouse Up releases the gesture before its prepared edit finishes.
        Session.CancelPreview();
        _previewPreparation.Cancel();
        Images.Cancel();
        _editCancellation?.Cancel();
    }

    private ICommand? FindShortcutCommand(Key key, bool control, bool shift) => (control, shift, key) switch
    {
        (true, false, Key.Z) => Undo,
        (true, _, Key.Y) or (true, true, Key.Z) => Redo,
        (true, true, Key.G) => Ungroup,
        (true, false, Key.G) => Group,
        (true, _, Key.A) => SelectAll,
        (true, _, Key.C) => Copy,
        (true, _, Key.V) => Paste,
        (true, _, Key.O) => OpenImage,
        (false, _, Key.Delete) => Delete,
        (false, _, Key.V) => SelectTool,
        (false, _, Key.H) => PanTool,
        (false, _, Key.R) => DrawTool,
        (false, _, Key.Enter) when Input.CanCompletePolygon => CompletePolygon,
        (false, _, Key.Back) when Input.PolygonPointCount > 0 => RemovePolygonPoint,
        (false, _, Key.F) => Fit,
        (false, _, Key.D0) => ResetAngle,
        _ => null
    };

    private bool TryNudgeSelection(Key key, bool shift)
    {
        if (!CanEdit || Input.IsDragging || Session.IsPreviewing || Input.PolygonPointCount > 0 ||
            !HasSelection() || key is not (Key.Left or Key.Right or Key.Up or Key.Down))
        {
            return false;
        }
        var step = shift ? 10 : 1;
        var delta = key switch
        {
            Key.Left => new PointD(-step, 0),
            Key.Right => new(step, 0),
            Key.Up => new(0, -step),
            _ => new(0, step)
        };
        if (Session.State.Rois.Count > 4096)
        {
            EditCompletion = ObserveEditAsync(PrepareEditAsync("ROI 이동", token => Session.PrepareTranslationAsync(delta, token)));
        }
        else
        {
            RunGuarded(() => Session.TranslateSelection(delta));
        }
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _editCancellation?.Cancel();
        _previewPreparation.Dispose();
        StyleSettings.Changed -= ApplyRoiSettings;
        Filters.Changed -= Publish;
        Inspections.Changed -= Publish;
        Matching.LocateRequested -= LocateInspection;
        Matching.SearchRangeRequested -= EnsurePatternSearchRange;
        Inspections.Shapes.LocateRequested -= LocateInspection;
        Live.Changed -= Refresh;
        Images.Changed -= Refresh;
        Layout.Changed -= LayoutChanged;
        Session.Changed -= SessionChanged;
        Input.Changed -= InputChanged;
        Filters.Dispose();
        Inspections.Dispose();
        Live.Dispose();
        Images.Dispose();
    }
}
