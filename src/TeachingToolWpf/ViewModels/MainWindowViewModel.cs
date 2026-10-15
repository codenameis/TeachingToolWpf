using System.IO;
using System.Windows.Input;
using TeachingToolWpf.Commands;
using TeachingToolWpf.Infrastructure;
using TeachingToolWpf.Models;
using TeachingToolWpf.Services;
using VisionCanvas.Core.Editing;
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
    public ViewportLayout Layout
    {
        get;
    }
    public RenderScene? Scene
    {
        get; private set;
    }
    public Func<Key, bool, bool, bool> KeyHandler => HandleKey;
    private readonly List<RelayCommand> _syncCommands = [];
    private readonly List<AsyncCommand> _asyncCommands = [];
    private readonly RoiClipboard _clipboard = new();
    private readonly RoiPreviewPreparation _previewPreparation;
    private CancellationTokenSource? _editCancellation;
    private bool _disposed;

    public Task PasteCompletion
    {
        get;
        private set;
    } = Task.CompletedTask;

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

    public RelayCommand PreviousRowPage
    {
        get;
    }

    public RelayCommand NextRowPage
    {
        get;
    }

    public RoiRow? FindRow(Guid id) => Rows.Find(id);

    public EditorSession Session
    {
        get;
    } = new();

    public InteractionController Input
    {
        get;
    }

    public RoiMaskViewModel Masks
    {
        get;
    }

    public ImageCoordinator Images
    {
        get;
    }

    public LivePreview Live
    {
        get;
    }

    public FilterPreviewViewModel Filters
    {
        get;
    }

    public ShapeMatchingViewModel Matching
    {
        get;
    }

    public InspectionToolsViewModel Inspections
    {
        get;
    }

    public IReadOnlyList<PixelKind> LiveFormats
    {
        get;
    } = [PixelKind.Gray8, PixelKind.Gray16, PixelKind.Rgba32, PixelKind.Bgra32];

    public PixelKind LiveFormat
    {
        get;
        set;
    } = PixelKind.Gray8;

    public AsyncCommand StartLive
    {
        get;
    }

    public AsyncCommand StopLive
    {
        get;
    }

    public RoiPropertiesViewModel Properties
    {
        get;
    }

    public RoiStyleSettingsViewModel StyleSettings
    {
        get;
    } = new();

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
    public bool HasRepeatedSelection => !Session.State.RepeatedSelection.IsEmpty;
    public bool CanUseOrdinaryRoiTools => !HasRepeatedSelection;

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

    public IReadOnlyList<ShapeOption> ShapeOptions
    {
        get;
    } = [new(RoiShapeKind.Rectangle, "사각형"), new(RoiShapeKind.Circle, "원"), new(RoiShapeKind.Triangle, "삼각형"), new(RoiShapeKind.Polygon, "폴리곤")];

    public RoiShapeKind SelectedShape
    {
        get => Input.DrawShape;
        set
        {
            Input.DrawShape = value;
            Notify();
        }
    }

    public RelayCommand CompletePolygon
    {
        get;
    }

    public RelayCommand RemovePolygonPoint
    {
        get;
    }

    public RelayCommand CancelPolygon
    {
        get;
    }

    public bool CanEdit => CanNavigate && !Images.IsBusy && _editCancellation is null;

    public bool CanNavigate => !_disposed;

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

    public string UndoHint => Session.CanUndo ? $"실행 취소: {Session.UndoName} (Ctrl+Z)" : "취소할 편집이 없습니다.";

    public string RedoHint => Session.CanRedo ? $"다시 실행: {Session.RedoName} (Ctrl+Y)" : "다시 실행할 편집이 없습니다.";

    public RelayCommand SelectTool
    {
        get;
    }

    public RelayCommand PanTool
    {
        get;
    }

    public RelayCommand DrawTool
    {
        get;
    }

    public RelayCommand AddRoi
    {
        get;
    }

    public RelayCommand ZoomIn
    {
        get;
    }

    public RelayCommand ZoomOut
    {
        get;
    }

    public RelayCommand ApplyZoom
    {
        get;
    }

    public RelayCommand Fit
    {
        get;
    }

    public RelayCommand ActualSize
    {
        get;
    }

    public RelayCommand Left
    {
        get;
    }

    public RelayCommand Right
    {
        get;
    }

    public RelayCommand Up
    {
        get;
    }

    public RelayCommand Down
    {
        get;
    }

    public AsyncCommand Delete
    {
        get;
    }

    public RelayCommand Copy
    {
        get;
    }

    public AsyncCommand Paste
    {
        get;
    }

    public AsyncCommand Group
    {
        get;
    }

    public AsyncCommand Ungroup
    {
        get;
    }

    public RelayCommand ResetAngle
    {
        get;
    }

    public RelayCommand RotateLeft
    {
        get;
    }

    public RelayCommand RotateRight
    {
        get;
    }

    public RelayCommand ApplyProperties
    {
        get;
    }

    public AsyncCommand SelectAll
    {
        get;
    }

    public RelayCommand CancelImport
    {
        get;
    }

    public AsyncCommand Initialize
    {
        get;
    }

    public AsyncCommand OpenImage
    {
        get;
    }

    public AsyncCommand Sample8K
    {
        get;
    }

    public AsyncCommand Sample24K
    {
        get;
    }

    public AsyncCommand PatternSample
    {
        get;
    }

    public AsyncCommand PrimitiveSample
    {
        get;
    }

    public AsyncCommand Undo
    {
        get;
    }

    public AsyncCommand Redo
    {
        get;
    }

    public AsyncCommand RestartRenderer
    {
        get;
    }

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
        _previewPreparation = new(Session, Fail, () => Status = "선택 영역 준비 중… 최신 포인터 위치 반영", preparePreview);
        Input = new(Session, CompletePreview, CompleteSelection, _previewPreparation.SubmitTranslation, _previewPreparation.SubmitRotation, CompleteRegionSelection);
        Masks = new(Session, Input, message => Status = message);
        Masks.Changed += Publish;
        Images = new(Session, display, layout, loader, Live.CloseAsync, PrepareRowsAsync);
        Inspections = inspections;
        Matching = inspections.Pattern;
        Inspections.Changed += Publish;
        Matching.LocateRequested += LocateInspection;
        Inspections.Shapes.LocateRequested += LocateInspection;
        Properties = new(Session);
        PreviousRowPage = Command(() => ChangeRowPage(_rowPage - 1), () => _rowPage > 0);
        NextRowPage = Command(() => ChangeRowPage(_rowPage + 1), () => (_rowPage + 1) * RowPageSize < Rows.Count);
        SelectTool = Command(() => SetTool(EditorTool.Select));
        PanTool = Command(() => SetTool(EditorTool.Pan), navigation: true);
        DrawTool = Command(() => StartRoiDrawing(SelectedGroupParent), CanCreateRoi);
        AddRoi = Command(AddRoiInView, CanCreateRoi);
        AddParentRoi = Command(AddParentRoiInView, HasImage);
        DrawParentRoi = Command(StartParentDrawing, HasImage);
        AddRepeatedTest = Command(AddRepeatedTestInDocument, HasImage);
        CompletePolygon = Command(Input.CompletePolygon, () => Input.CanCompletePolygon);
        RemovePolygonPoint = Command(Input.RemovePolygonPoint, () => Input.PolygonPointCount > 0);
        CancelPolygon = Command(Input.Cancel, () => Input.PolygonPointCount > 0);
        ZoomIn = Command(() => Zoom(1.25), HasImage, true);
        ZoomOut = Command(() => Zoom(.8), HasImage, true);
        ApplyZoom = Command(SetZoom, HasImage, true);
        Fit = Command(FitImage, HasImage, true);
        ActualSize = Command(() => ZoomTo(1 / Layout.Dpi), HasImage, true);
        Left = Command(() => Pan(100, 0), navigation: true);
        Right = Command(() => Pan(-100, 0), navigation: true);
        Up = Command(() => Pan(0, 100), navigation: true);
        Down = Command(() => Pan(0, -100), navigation: true);
        Delete = Async(() => EditCompletion = PrepareEditAsync("선택 ROI 삭제", Session.PrepareDeleteAsync), HasSelection);
        Copy = Command(CopySelection, HasSelection);
        Paste = Async(() => PasteCompletion = PasteAsync(), () => HasImage() && _clipboard.HasContent);
        Group = Async(() => EditCompletion = PrepareEditAsync("부모 ROI로 그룹화", Session.PrepareGroupAsync), () => !HasRepeatedSelection && Session.State.SelectionFacts.CanGroup);
        ChooseChildRois = Command(ChooseRoisToAttach, () => HasSelection() && !HasRepeatedSelection &&
            Session.State.Selection.All(id => Session.State.Rois[id].ParentId is null));
        AttachToParent = Async(AttachRoisToParentAsync, () => _roisToAttach.Length > 0 && CanAttachToSelectedRoi());
        Ungroup = Async(() => EditCompletion = PrepareEditAsync("그룹 해제", Session.PrepareUngroupAsync), () => !HasRepeatedSelection && Session.State.SelectionFacts.HasGroup);
        ResetAngle = Command(Session.ResetAngles, () => Session.State.SelectionCount == 1 && !HasRepeatedSelection);
        RotateLeft = Command(() => Rotate(-15), () => Session.State.SelectionCount == 1);
        RotateRight = Command(() => Rotate(15), () => Session.State.SelectionCount == 1);
        ApplyProperties = Command(Properties.Apply, () => Properties.HasSingleSelection);
        SelectAll = Async(() => EditCompletion = PrepareEditAsync("전체 선택", Session.PrepareSelectAllAsync), () => Session.State.RoiCount != 0);
        CancelImport = new(Images.Cancel, () => Images.IsBusy);
        Initialize = Async(InitializeAsync);
        OpenImage = Async(OpenAsync, () => _selectImage is not null);
        Sample8K = Async(() => LoadSampleAsync(8192, 8192));
        Sample24K = Async(() => LoadSampleAsync(24576, 32768));
        PatternSample = Async(LoadPatternSampleAsync);
        PrimitiveSample = Async(async () =>
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "primitive-shapes.png");
            await Images.ReplaceAsync(new(path, "일반 도형 샘플", 640, 480, ImageFileVersion.Read(path)));
            if (Session.State.Image?.Key == path)
            {
                Inspections.Selected = InspectionToolKind.PrimitiveShapes;
            }
        }, () => Inspections.IsIdle);
        Undo = Async(() => EditCompletion = HistoryAsync(false), () => Session.CanUndo);
        Redo = Async(() => EditCompletion = HistoryAsync(true), () => Session.CanRedo);
        StartLive = Async(StartLiveAsync, () => !Live.IsRunning);
        StopLive = Async(Live.StopAsync, () => Live.IsRunning);
        RestartRenderer = Async(display.RestartAsync);
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

    private RelayCommand Command(Action action, Func<bool>? available = null, bool navigation = false)
    {
        var command = new RelayCommand(() => Guard(action), () => (navigation ? CanNavigate : CanEdit) && !Input.IsDragging && (available?.Invoke() ?? true));
        _syncCommands.Add(command);
        return command;
    }

    private AsyncCommand Async(Func<Task> action, Func<bool>? available = null)
    {
        var command = new AsyncCommand(action, Fail, () => CanEdit && !Input.IsDragging && (available?.Invoke() ?? true));
        _asyncCommands.Add(command);
        return command;
    }

    public void Guard(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    public void Fail(Exception ex) => Status = ex.Message;

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
        if (Session.IsPreviewing || (ReferenceEquals(_rowVersion, state.Snapshot.RowVersion) && ReferenceEquals(_repeatedRowVersion, state.RepeatedRowVersion)))
        {
            return;
        }

        if (_preparedRows is { } prepared && ReferenceEquals(prepared.Version, state.Snapshot.RowVersion) && ReferenceEquals(prepared.RepeatedVersion, state.RepeatedRowVersion))
        {
            Rows = prepared.Rows;
        }
        else if (!ReferenceEquals(_repeatedRowVersion, state.RepeatedRowVersion) || !Rows.TryRefreshMetadata(_rowRois, state))
        {
            Rows = new(RoiRow.Build(state));
        }

        _rowVersion = state.Snapshot.RowVersion;
        _repeatedRowVersion = state.RepeatedRowVersion;
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
        Input.NavigationOnly = !CanEdit;
        Masks.Update(CanEdit);
        Filters.Update(Session.State, Live.Image is not null, Session.TransformPreview, DisplayImage?.Pixels ?? PixelKind.Gray8);
        Inspections.Update(Session.State, Images.Generation, Live.Image is not null, CanEdit && !Live.IsRunning && !Session.IsPreviewing && !HasRepeatedSelection);
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
        var fitted = ViewportTransform.Fit(Math.Max(1, bounds.Width), Math.Max(1, bounds.Height), Layout.Width, Layout.Height);
        var scale = Math.Min(fitted.Scale, 8 / Layout.Dpi);
        Session.SetViewport(new(scale, Center - (bounds.Center * scale)));
    }

    private void Rotate(double angle)
    {
        Session.BeginPreview();
        _previewPreparation.SubmitRotation(Session.State.RepeatedSelection.IsEmpty ? Session.State.SelectedSpatial.Bounds.Center : Session.State.TransformSelection.Bounds.Center, angle);
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

    private Task LoadPatternSampleAsync()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "pattern-matching.png");
        return Images.ReplaceAsync(new(path, "", 0, 0, ImageFileVersion.Read(path)));
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
            SelectedMaskIndex = Masks.SelectedIndex,
            MaskEditing = Input.Tool == EditorTool.Mask
        };
        Notify(nameof(Scene));
    }

    public void SelectRows(IEnumerable<Guid> ids, bool preserveRepeatedRanges = false)
    {
        if (CanEdit && !Input.IsDragging)
        {
            var captured = ids.ToArray();
            if (captured.Length <= SynchronousPreviewRoiLimit || Session.State.Rois.Count <= SynchronousSelectionDocumentLimit)
            {
                Session.SelectEntries(captured, preserveRepeatedRanges);
            }
            else
            {
                EditCompletion = ObserveEditAsync(PrepareEditAsync("목록 선택", token => Session.PrepareEntrySelectionAsync(captured, token, preserveRepeatedRanges)));
            }
        }
    }

    public bool HandleKey(Key key, bool control, bool shift)
    {
        if (key == Key.Escape)
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
            return true;
        }
        ICommand? command = (control, shift, key) switch
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
        if (command is not null)
        {
            if (command.CanExecute(null))
            {
                command.Execute(null);
            }

            return true;
        }
        if (CanEdit && !Input.IsDragging && HasSelection() && key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
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
                Guard(() => Session.TranslateSelection(delta));
            }

            return true;
        }
        return false;
    }

    public void Dispose()
    {
        _disposed = true;
        _editCancellation?.Cancel();
        _previewPreparation.Dispose();
        StyleSettings.Changed -= ApplyRoiSettings;
        Filters.Changed -= Publish;
        Filters.Dispose();
        Inspections.Changed -= Publish;
        Matching.LocateRequested -= LocateInspection;
        Inspections.Shapes.LocateRequested -= LocateInspection;
        Inspections.Dispose();
        Live.Dispose();
        Live.Changed -= Refresh;
        Images.Dispose();
        Layout.Changed -= LayoutChanged;
        Session.Changed -= SessionChanged;
        Input.Changed -= InputChanged;
        Images.Changed -= Refresh;
    }
}
