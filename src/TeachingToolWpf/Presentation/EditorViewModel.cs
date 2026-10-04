using System.Windows.Input;
using Microsoft.Win32;
using TeachingToolWpf.Core.Editing;
using TeachingToolWpf.Core.Geometry;
using TeachingToolWpf.Core.Imaging;
using TeachingToolWpf.Core.Interaction;
using TeachingToolWpf.Imaging;

namespace TeachingToolWpf.Presentation;

/// <summary>Presentation state and command availability; editing rules remain in the session.</summary>
public sealed partial class EditorViewModel : ObservableObject, IDisposable
{
    private readonly IViewportSurface _viewport;
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

    private readonly System.Windows.Threading.DispatcherTimer _filterTimer;
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

    public string SelectionSummary => $"{Session.State.Selection.Count}개 선택 / ROI {Session.State.Rois.Count}개";

    public int RoiCount => Session.State.Rois.Count;

    public bool HasRois => RoiCount > 0;

    public bool IsLiveRunning => Live.IsRunning;

    public string SelectionTitle => Session.State.Selection.Count switch { 0 => "선택한 영역 없음", 1 => "영역 속성", _ => "여러 영역 선택" };

    public string ToolHint => Input.Tool switch
    {
        EditorTool.Draw when Input.DrawShape == RoiShapeKind.Polygon => $"꼭짓점 {Input.PolygonPointCount}개 · 클릭으로 추가 · 완료/Enter 또는 첫 점 클릭 · 취소: Esc",
        EditorTool.Draw => $"{(Input.DrawParentId is null ? "부모" : "자식")} ROI 그리기 · 완료 후 선택 도구 복귀 · 취소: Esc",
        EditorTool.Pan => "드래그해 화면을 이동하세요. 휠로 확대·축소합니다.",
        _ => "클릭으로 선택 · Ctrl로 다중 선택 · 휠로 확대·축소"
    };

    public string ToolName => Input.Tool switch { EditorTool.Pan => "이동", EditorTool.Draw => Input.DrawParentId is null ? "부모 ROI 그리기" : "자식 ROI 그리기", _ => "선택" };

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

    public RelayCommand Group
    {
        get;
    }

    public RelayCommand Ungroup
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

    public EditorViewModel(IViewportSurface viewport, IImageLoader loader, LivePreview live, Imaging.Filters.IRoiFilterPreview filterPreview,
        Imaging.Matching.IShapeMatchingService? shapeMatching = null)
        : this(viewport, loader, live, filterPreview, null, shapeMatching)
    {
    }

    internal EditorViewModel(IViewportSurface viewport, IImageLoader loader, LivePreview live, Imaging.Filters.IRoiFilterPreview filterPreview,
        Action<DocumentState, CancellationToken>? preparePreview)
        : this(viewport, loader, live, filterPreview, preparePreview, null)
    {
    }

    private EditorViewModel(IViewportSurface viewport, IImageLoader loader, LivePreview live, Imaging.Filters.IRoiFilterPreview filterPreview,
        Action<DocumentState, CancellationToken>? preparePreview, Imaging.Matching.IShapeMatchingService? shapeMatching)
    {
        _viewport = viewport;
        StyleSettings.Changed += Publish;
        Live = live;
        Filters = new(filterPreview, Session);
        Filters.Changed += Publish;
        _filterTimer = new(TimeSpan.FromMilliseconds(200), System.Windows.Threading.DispatcherPriority.Background,
            (_, _) =>
        {
            if (Live.IsRunning)
            {
                Filters.RefreshLiveFrame();
            }
        }, System.Windows.Threading.Dispatcher.CurrentDispatcher);
        _previewPreparation = new(Session, Fail, () => Status = "선택 영역 준비 중… 최신 포인터 위치 반영", preparePreview);
        Input = new(Session, CompletePreview, CompleteSelection, _previewPreparation.SubmitTranslation, _previewPreparation.SubmitRotation);
        Images = new(Session, viewport, loader, Live.CloseAsync, PrepareRowsAsync);
        Matching = new(shapeMatching ?? new Imaging.Matching.ShapeMatchingService(loader, () => null));
        Matching.Changed += Publish;
        Matching.LocateRequested += LocateInspection;
        Properties = new(Session);
        PreviousRowPage = Command(() => ChangeRowPage(_rowPage - 1), () => _rowPage > 0);
        NextRowPage = Command(() => ChangeRowPage(_rowPage + 1), () => (_rowPage + 1) * RowPageSize < Rows.Count);
        SelectTool = Command(() => SetTool(EditorTool.Select));
        PanTool = Command(() => SetTool(EditorTool.Pan), navigation: true);
        DrawTool = Command(() => StartRoiDrawing(null), HasImage);
        AddRoi = Command(AddRoiInView, HasImage);
        AddChildRoi = Command(AddChildRoiInView, CanAddChildRoi);
        DrawChildRoi = Command(() => StartRoiDrawing(Session.State.Selection.Single()), CanAddChildRoi);
        CompletePolygon = Command(Input.CompletePolygon, () => Input.CanCompletePolygon);
        RemovePolygonPoint = Command(Input.RemovePolygonPoint, () => Input.PolygonPointCount > 0);
        CancelPolygon = Command(Input.Cancel, () => Input.PolygonPointCount > 0);
        ZoomIn = Command(() => Zoom(1.25), HasImage, true);
        ZoomOut = Command(() => Zoom(.8), HasImage, true);
        ApplyZoom = Command(SetZoom, HasImage, true);
        Fit = Command(FitImage, HasImage, true);
        ActualSize = Command(() => ZoomTo(1 / viewport.Dpi), HasImage, true);
        Left = Command(() => Pan(100, 0), navigation: true);
        Right = Command(() => Pan(-100, 0), navigation: true);
        Up = Command(() => Pan(0, 100), navigation: true);
        Down = Command(() => Pan(0, -100), navigation: true);
        Delete = Async(() => EditCompletion = PrepareEditAsync("선택 ROI 삭제", Session.PrepareDeleteAsync), HasSelection);
        Copy = Command(CopySelection, HasSelection);
        Paste = Async(() => PasteCompletion = PasteAsync(), () => HasImage() && _clipboard.HasContent);
        Group = Command(GroupSelection, () => Session.State.SelectionFacts.HasMultipleRoots);
        Ungroup = Command(Session.UngroupSelection, () => Session.State.SelectionFacts.HasGroup);
        ResetAngle = Command(Session.ResetAngles, HasSelection);
        RotateLeft = Command(() => Rotate(-15), HasSelection);
        RotateRight = Command(() => Rotate(15), HasSelection);
        ApplyProperties = Command(Properties.Apply, () => Properties.HasSingleSelection);
        SelectAll = Async(() => EditCompletion = PrepareEditAsync("전체 선택", Session.PrepareSelectAllAsync), () => Session.State.Rois.Count != 0);
        CancelImport = new(Images.Cancel, () => Images.IsBusy);
        OpenImage = Async(OpenAsync);
        Sample8K = Async(() => LoadSampleAsync(8192, 8192));
        Sample24K = Async(() => LoadSampleAsync(24576, 32768));
        Undo = Async(() => EditCompletion = HistoryAsync(false), () => Session.CanUndo);
        Redo = Async(() => EditCompletion = HistoryAsync(true), () => Session.CanRedo);
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

    private void CopySelection()
    {
        var copiedCount = _clipboard.Copy(Session.State);
        Status = $"ROI {copiedCount}개 복사 · Ctrl+V로 붙여넣기";
        RefreshCommands();
    }

    private void GroupSelection()
    {
        var selectedCount = Session.State.Selection.Count;
        var parentId = Session.GroupSelection();
        var descendantCount = RoiHierarchy.Subtree(Session.State, [parentId]).Count - 1;
        Status = $"새 부모를 만들었습니다. 포함 ROI {descendantCount}개 · 선택 {selectedCount}개";
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

    private bool HasSelection() => Session.State.Selection.Count > 0;

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
        ZoomText = (state.Viewport.Scale * _viewport.Dpi * 100).ToString("0.##");
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
        Input.NavigationOnly = !CanEdit;
        Filters.Update(Session.State, Live.Image is not null, Session.TransformPreview);
        Matching.Update(Session.State, Images.Generation, Live.Image is not null, CanEdit && !Live.IsRunning && !Session.IsPreviewing);
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

    private PointD Center => new(_viewport.ViewWidth / 2, _viewport.ViewHeight / 2);

    private void Pan(double x, double y) => Session.SetViewport(Session.State.Viewport.Pan(new(x, y)));

    private void Zoom(double factor) => Session.SetViewport(Session.State.Viewport.ZoomAt(Center, factor));

    private void ZoomTo(double scale) => Zoom(scale / Session.State.Viewport.Scale);

    private void SetZoom()
    {
        if (!double.TryParse(ZoomText.TrimEnd('%'), out var percent) || !double.IsFinite(percent) || percent <= 0)
        {
            throw new ArgumentException("배율에는 0보다 큰 숫자를 입력하세요.");
        }

        ZoomTo(percent / (100 * _viewport.Dpi));
    }

    private void FitImage()
    {
        if (DisplayImage is { } image)
        {
            Session.SetViewport(ViewportTransform.Fit(image.Width, image.Height, _viewport.ViewWidth, _viewport.ViewHeight));
        }
    }

    private void LocateInspection(BoundsD bounds)
    {
        var fitted = ViewportTransform.Fit(Math.Max(1, bounds.Width), Math.Max(1, bounds.Height), _viewport.ViewWidth, _viewport.ViewHeight);
        var scale = Math.Min(fitted.Scale, 8 / _viewport.Dpi);
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
        var dialog = new OpenFileDialog { Title = "배경 이미지 열기 · 대용량 이미지는 최초 캐시 생성이 필요합니다", Filter = "이미지|*.png;*.jpg;*.jpeg;*.tif;*.tiff;*.webp;*.v|모든 파일|*.*" };
        if (dialog.ShowDialog() == true)
        {
            await Images.ReplaceAsync(new(dialog.FileName, "", 0, 0, DiskPyramidSource.Version(dialog.FileName)));
        }
    }

    private Task LoadSampleAsync(int width, int height) => Images.ReplaceAsync(new($"sample:{width}:{height}", "", width, height));

    public void Publish()
    {
        Input.SetViewportSize(_viewport.ViewWidth, _viewport.ViewHeight);
        _viewport.Submit(new(Session.State, DisplayImage, Images.Generation, _viewport.ViewWidth, _viewport.ViewHeight, _viewport.Dpi, Input.Overlay, PointSampling, Live.Image is not null, Filters.Result, Session.TransformPreview, StyleSettings.Palette, Matching.Overlay));
    }

    public void SelectRows(IEnumerable<Guid> ids)
    {
        if (CanEdit && !Input.IsDragging)
        {
            Session.Select(ids);
        }
    }

    public bool HandleKey(Key key, bool control, bool shift)
    {
        if (key == Key.Escape)
        {
            Input.Cancel();
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
        StyleSettings.Changed -= Publish;
        _filterTimer.Stop();
        Filters.Changed -= Publish;
        Filters.Dispose();
        Matching.Changed -= Publish;
        Matching.LocateRequested -= LocateInspection;
        Matching.Dispose();
        Live.Dispose();
        Live.Changed -= Refresh;
        Images.Dispose();
        _viewport.LayoutChanged -= Publish;
        Session.Changed -= SessionChanged;
        Input.Changed -= InputChanged;
        Images.Changed -= Refresh;
    }
}
