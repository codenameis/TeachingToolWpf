using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using TeachingToolWpf.Commands;
using TeachingToolWpf.Infrastructure;
using TeachingToolWpf.MassiveModels;
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
    private const int MassiveModelPageSize = 512; // [Perf Why]: Bounds virtualized page slice for 100M ROI models to 512 rows per page (< 0.1 ms on-demand slice).
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
    private MassiveRoiModelFile? _activeMassiveModel;
    private bool _suppressMassiveModelReset;
    private bool _disposed;

    public MassiveRoiModelFile? ActiveMassiveModel => _activeMassiveModel;

    public bool HasActiveMassiveModel => _activeMassiveModel is not null;

    public Task PasteCompletion { get; private set; } = Task.CompletedTask;

    private string _status = "이미지를 준비하고 있습니다…";

    private string _gpuStatus = "DX12 초기화 중…";

    private string _zoomText = "100";
    private bool _pointSampling;
    private object? _rowVersion;
    private System.Collections.Immutable.ImmutableDictionary<Guid, Roi>? _rowRois;
    private RoiRows _rows = new([]);
    // Small documents keep the virtualized list stable; paging bounds WPF's unresolved multi-selection scan at scale.
    private int RowPageSize => _activeMassiveModel is not null ? MassiveModelPageSize : (Rows.Count <= 4096 ? 4096 : 512);

    private long TotalLogicalRowCount => _activeMassiveModel?.Header.TotalRoiCount ?? Rows.Count;

    private int _rowPage;
    private RoiRows _visibleRows = new([]);
    private object? _rowSelection;
    private bool _wasPreviewing;
    private RoiTransformPreview? _lastTransformPreview;

    public RoiRows VisibleRows => _visibleRows;

    public string RowPageLabel => $"{_rowPage + 1:N0} / {Math.Max(1L, (TotalLogicalRowCount + RowPageSize - 1) / RowPageSize):N0} 페이지";

    public event Action? RowPageChanged;

    public RelayCommand PreviousRowPage { get; private set; }

    public RelayCommand NextRowPage { get; private set; }

    public RoiRow? FindRow(Guid id)
    {
        if ( _visibleRows.Find(id) is { } visible )
        {
            return visible;
        }

        if (_activeMassiveModel is { } massive && massive.TryResolveIndex(id, out long idx))
        {
            RoiRow row = massive.GetEntry(idx).ToRoiRow();
            if (Inspections.RoiAssignments.TryGetValue(id, out RoiAlgorithmAssignment? custom))
            {
                row = row with
                {
                    AlgorithmBadge = custom.AlgorithmBadge,
                    AlgorithmAccent = custom.AlgorithmAccent,
                    VerdictBadge = custom.VerdictBadge,
                    VerdictAccent = custom.VerdictAccent,
                    PatternThumbnail = custom.PatternThumbnail
                };
            }

            return row;
        }

        return Rows.Find(id);
    }

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

    public PatternMaskEditorViewModel PatternMaskEditor { get; }

    public event Action? PatternMaskWindowRequested;

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

    public string SelectionSummary => $"{Session.State.SelectionCount:N0}개 선택 / ROI {RoiCount:N0}개";

    public long RoiCount => _activeMassiveModel?.Header.TotalRoiCount ?? Session.State.RoiCount;

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

    public AsyncCommand FewShotSample { get; private set; }

    public AsyncCommand SuiteSample { get; private set; }

    public AsyncCommand Undo { get; private set; }

    public AsyncCommand Redo { get; private set; }

    public AsyncCommand RestartRenderer { get; private set; }

    public RelayCommand SaveTeachingRecipe { get; private set; }

    public RelayCommand LoadTeachingRecipe { get; private set; }

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
        Filters.ActivePipelineChanged += SyncPreInspectionFilterPipeline;
        _previewPreparation = new(Session, ReportError, () => Status = "선택 영역 준비 중… 최신 포인터 위치 반영", preparePreview);
        Input = new(Session, CompletePreview, CompleteSelection, _previewPreparation.SubmitTranslation, _previewPreparation.SubmitRotation, CompleteRegionSelection);
        Navigation = new(Session, Input, Layout, () => CanEdit);
        Masks = new(Session, Input, message => Status = message);
        Masks.Changed += Publish;
        Masks.PatternMaskEditorRequested += RequestPatternMaskWindow;
        Images = new(Session, display, layout, loader, Live.CloseAsync, PrepareRowsAsync);
        PatternMaskEditor = new(Session, loader, () => null, message => Status = message);
        Inspections = inspections;
        Matching = inspections.Pattern;
        SyncPreInspectionFilterPipeline(Filters.BuildEffectivePipeline(), Filters.PreFilterBeforeInspection);
        Inspections.Changed += Publish;
        Inspections.AssignmentsChanged += RefreshRowAssignments;
        Inspections.LocateBoundsRequested += LocateInspection;
        Matching.LocateRequested += LocateInspection;
        Matching.SearchRangeRequested += EnsurePatternSearchRange;
        Matching.PatternMaskEditorRequested += RequestPatternMaskWindow;
        Inspections.Shapes.LocateRequested += LocateInspection;
        if (Inspections.FewShot is { } fewShot)
        {
            fewShot.LocateRequested += LocateInspection;
        }
        if (Inspections.Suite is { } suite)
        {
            suite.LocateRequested += LocateInspection;
            suite.SampleLoadRequested += RequestSuiteSampleLoad;
            suite.RoiOcvSettingsChangeRequested += ApplyRoiOcvSettings;
        }
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

    public Task RefreshPatternMaskEditorAsync()
        => PatternMaskEditor.LoadSelectedRoiAsync(Session.State.Image, Live.IsRunning);

    private void RequestPatternMaskWindow()
        => PatternMaskWindowRequested?.Invoke();

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
        var isPreviewing = Session.IsPreviewing;
        var previewingTransition = _wasPreviewing != isPreviewing;
        _wasPreviewing = isPreviewing;
        if (!isPreviewing)
        {
            _previewPreparation.Cancel();
        }

        var state = Session.State;
        var transformPreview = Session.TransformPreview;
        var viewOnlyNavigation = !isPreviewing && !previewingTransition &&
            ReferenceEquals(_rowRois, state.Rois) &&
            ReferenceEquals(_rowSelection, state.Selection) &&
            _lastTransformPreview == transformPreview;
        _lastTransformPreview = transformPreview;
        if (viewOnlyNavigation)
        {
            ZoomText = (state.Viewport.Scale * Layout.Dpi * 100).ToString("0.##");
            Refresh();
            return;
        }
        if (isPreviewing && !previewingTransition && ReferenceEquals(_rowSelection, state.Selection))
        {
            Properties.Refresh();
            Filters.Update(state, Live.Image is not null, transformPreview, DisplayImage?.Pixels ?? PixelKind.Gray8);
            Inspections.Update(state, Images.Generation, Live.Image is not null, false, transformPreview);
            Publish();
            return;
        }

        RefreshRowsForDocument(state);
        RevealSingleSelection(state);
        _rowSelection = state.Selection;
        if (!isPreviewing)
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

        if (!_suppressMassiveModelReset && state.Rois.IsEmpty && _activeMassiveModel is not null)
        {
            _activeMassiveModel = null;
            Notify(nameof(ActiveMassiveModel));
            Notify(nameof(HasActiveMassiveModel));
        }

        if (_activeMassiveModel is not null)
        {
            _rowVersion = state.Snapshot.RowVersion;
            int maxMassivePage = (int)Math.Max(0L, (TotalLogicalRowCount - 1) / RowPageSize);
            ChangeRowPage(Math.Min(_rowPage, maxMassivePage), force: true);
            return;
        }

        if (_preparedRows is { } prepared && ReferenceEquals(prepared.Version, state.Snapshot.RowVersion))
        {
            Rows = prepared.Rows;
            Rows.ApplyAssignments(Inspections.RoiAssignments);
        }
        else if (!Rows.TryRefreshMetadata(_rowRois, state, Inspections.RoiAssignments))
        {
            Rows = new(RoiRow.Build(state, default, Inspections.RoiAssignments));
        }

        _rowVersion = state.Snapshot.RowVersion;
        var lastPage = Math.Max(0, (Rows.Count - 1) / RowPageSize);
        ChangeRowPage(Math.Min(_rowPage, lastPage), force: true);
    }

    private void RefreshRowAssignments()
    {
        if (_disposed)
        {
            return;
        }

        if (_activeMassiveModel is not null)
        {
            ChangeRowPage(_rowPage, force: true);
            return;
        }

        Rows.ApplyAssignments(Inspections.RoiAssignments);
        ChangeRowPage(_rowPage, force: true);
    }

    private void RevealSingleSelection(DocumentState state)
    {
        // pan/zoom은 사용자가 열어 둔 목록 페이지를 바꾸지 않는다.
        if (ReferenceEquals(_rowSelection, state.Selection) || state.Selection.Count != 1)
        {
            return;
        }

        Guid selectedId = state.Selection.First();
        if (_activeMassiveModel is { } massive && massive.TryResolveIndex(selectedId, out long massiveIndex))
        {
            ChangeRowPage((int)(massiveIndex / RowPageSize));
            return;
        }

        var index = Rows.IndexOf(selectedId);
        if (index >= 0)
        {
            ChangeRowPage(index / RowPageSize);
        }
    }

    public void JumpToRowPage(int pageIndex)
    {
        int maxPage = (int)Math.Max(0L, (TotalLogicalRowCount - 1) / RowPageSize);
        ChangeRowPage(Math.Clamp(pageIndex, 0, maxPage), force: true);
    }

    private void ChangeRowPage(int page, bool force = false)
    {
        if (!force && page == _rowPage)
        {
            return;
        }

        _rowPage = page;
        _visibleRows = _activeMassiveModel is { } massive
            ? massive.BuildPageRows(page, RowPageSize, Inspections.RoiAssignments)
            : Rows.Page(page, RowPageSize);
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
        Inspections.Update(Session.State, Images.Generation, Live.Image is not null, CanEdit && !Live.IsRunning && !Session.IsPreviewing, Session.TransformPreview);
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
        Scene = new(Session.State, DisplayImage, Images.Generation, Layout.Width, Layout.Height, Layout.Dpi, Input.Overlay, PointSampling, Live.Image is not null, Inspections.FilterPreview ?? Filters.Result, Session.TransformPreview, StyleSettings.Palette, Inspections.Overlay)
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
            if (_activeMassiveModel is { } massive && captured.Length > 0 && captured.Length <= 64)
            {
                foreach (var id in captured)
                {
                    if (!Session.State.Rois.ContainsKey(id) && massive.TryResolveIndex(id, out long idx))
                    {
                        MassiveRoiEntry entry = massive.GetEntry(idx);
                        Roi materialized = new Roi(entry.Id, entry.Name, entry.Geometry, null, false, idx + 1)
                        {
                            IsNameVisible = false
                        };
                        Session.EnsureNamedRoiPresent(materialized);
                        Inspections.RecordSingleAssignment(Session.State, entry.ToAssignment());
                    }
                }
            }

            if (captured.Length <= SynchronousPreviewRoiLimit || Session.State.Rois.Count <= SynchronousSelectionDocumentLimit)
            {
                Session.Select(captured);
                FocusSelectedRowBounds(captured);
            }
            else
            {
                EditCompletion = ObserveEditAsync(PrepareEditAsync("목록 선택", token => Session.PrepareSelectionAsync(captured, token)));
            }
        }
    }

    public async Task<MassiveRoiModelFile> LoadMassiveModelFromFileAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        long totalStart = Stopwatch.GetTimestamp();

        MassiveRoiModelFile modelFile = await MassiveRoiModelSerializer.LoadFromDiskAsync(filePath, cancellationToken);
        ApplyLoadedMassiveModel(modelFile, totalStart);
        return modelFile;
    }

    public MassiveRoiModelFile LoadMassiveModelFromFile(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        long totalStart = Stopwatch.GetTimestamp();

        MassiveRoiModelFile modelFile = MassiveRoiModelSerializer.LoadFromDisk(filePath);
        ApplyLoadedMassiveModel(modelFile, totalStart);
        return modelFile;
    }

    private void ApplyLoadedMassiveModel(MassiveRoiModelFile modelFile, long totalStartTimestamp)
    {
        _suppressMassiveModelReset = true;
        try
        {
            if (Session.State.Image is null)
            {
                int w = modelFile.Header.ImageWidth;
                int h = modelFile.Header.ImageHeight;
                Session.InitializeImage(
                    new ImageReference($"sample:{w}:{h}", $"{w:N0} × {h:N0} 합성 모델 캔버스", w, h),
                    ViewportTransform.Fit(w, h, Math.Max(1.0, Layout.Width), Math.Max(1.0, Layout.Height)));
            }

            IReadOnlyList<MassiveRoiEntry> anchors = modelFile.CanvasAnchors;
            Roi[] anchorRois = new Roi[anchors.Count];
            RoiAlgorithmAssignment[] anchorAssignments = new RoiAlgorithmAssignment[anchors.Count];
            for (int i = 0; i < anchors.Count; i++)
            {
                MassiveRoiEntry entry = anchors[i];
                anchorRois[i] = new Roi(entry.Id, entry.Name, entry.Geometry, null, false, entry.Index + 1)
                {
                    IsNameVisible = false
                };
                anchorAssignments[i] = entry.ToAssignment();
            }

            _activeMassiveModel = modelFile;
            _rowPage = 0;
            Session.ReplaceWithNamedRois(modelFile.Header.ModelName, anchorRois, selectAdded: false);
            Inspections.ReplaceMassiveModelAssignments(Session.State, anchorAssignments);
            FitImage();
            SetTool(EditorTool.Select);
        }
        finally
        {
            _suppressMassiveModelReset = false;
        }

        double totalElapsedMs = Stopwatch.GetElapsedTime(totalStartTimestamp).TotalMilliseconds;
        Notify(nameof(ActiveMassiveModel));
        Notify(nameof(HasActiveMassiveModel));
        Notify(nameof(RoiCount));
        Notify(nameof(SelectionSummary));
        ChangeRowPage(0, force: true);
        Refresh();

        Status = $"[{modelFile.Header.ModelName}] 디스크 직접 로드 완료 ({totalElapsedMs:F1} ms, 파일 I/O {modelFile.LoadElapsedMilliseconds:F1} ms) · 총 {modelFile.Header.TotalRoiCount:N0}개 랜덤 ROI & 알고리즘 배정";
    }

    private void FocusSelectedRowBounds(IReadOnlyList<Guid> ids)
    {
        if (ids.Count == 0 || ids.Count > 64 || !CanNavigate || Input.IsDragging || Session.IsPreviewing)
        {
            return;
        }

        BoundsD? combined = null;
        foreach (var id in ids)
        {
            if (Session.State.Rois.TryGetValue(id, out var roi))
            {
                var roiBounds = roi.Geometry.Bounds;
                combined = combined is { } existing
                    ? new BoundsD(
                        Math.Min(existing.Left, roiBounds.Left),
                        Math.Min(existing.Top, roiBounds.Top),
                        Math.Max(existing.Right, roiBounds.Right),
                        Math.Max(existing.Bottom, roiBounds.Bottom))
                    : roiBounds;
            }
        }

        if (combined is { Width: > 0, Height: > 0 } bounds)
        {
            var padX = Math.Max(bounds.Width * 0.45, 24.0);
            var padY = Math.Max(bounds.Height * 0.45, 24.0);
            var padded = new BoundsD(
                bounds.Left - padX,
                bounds.Top - padY,
                bounds.Right + padX,
                bounds.Bottom + padY);
            LocateInspection(padded);
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

    private void SyncPreInspectionFilterPipeline(
        System.Collections.Immutable.ImmutableArray<FilterSettings> pipeline,
        bool preFilterBeforeInspection)
    {
        if (!preFilterBeforeInspection || pipeline.IsDefaultOrEmpty)
        {
            TeachingToolWpf.Imaging.Matching.PreInspectionFilterPipeline.ClearActivePipeline();
            Inspections.UpdatePreFilterStatus(Filters.PipelineSummaryText, hasEnabledSteps: false);
            return;
        }

        var builder = System.Collections.Immutable.ImmutableArray.CreateBuilder<TeachingToolWpf.Imaging.Matching.PreInspectionFilterStep>(pipeline.Length);
        foreach (var step in pipeline)
        {
            if (step.IsEnabled && step.Kind != RoiFilterKind.None)
            {
                builder.Add(new TeachingToolWpf.Imaging.Matching.PreInspectionFilterStep(
                    Kind: (TeachingToolWpf.Imaging.Matching.PreInspectionFilterKind)(int)step.Kind,
                    Threshold: step.Threshold,
                    EdgeLow: step.EdgeLow,
                    EdgeHigh: step.EdgeHigh,
                    Radius: step.Radius,
                    Sigma: step.Sigma,
                    IsEnabled: true));
            }
        }

        var mapped = builder.ToImmutable();
        TeachingToolWpf.Imaging.Matching.PreInspectionFilterPipeline.SetActivePipeline(mapped);
        Inspections.UpdatePreFilterStatus(Filters.PipelineSummaryText, hasEnabledSteps: !mapped.IsEmpty);
    }

    private void ApplyRoiOcvSettings(IReadOnlyList<Guid> roiIds, RoiOcvSettings settings)
    {
        if (_disposed || roiIds.Count == 0)
        {
            return;
        }

        RunGuarded(() => Session.SetOcvSettings(roiIds, settings));
    }

    public void SaveTeachingRecipeToDefaultFile()
    {
        var rois = new List<TeachingRecipeRoiEntry>();
        foreach (var roi in Session.State.Rois.Values)
        {
            if (Session.State.IsGroupRoi(roi.Id))
            {
                continue;
            }

            rois.Add(new TeachingRecipeRoiEntry(
                Name: roi.Name,
                Kind: roi.Geometry.Shape.Kind,
                CenterX: roi.Geometry.Center.X,
                CenterY: roi.Geometry.Center.Y,
                Width: roi.Geometry.Width,
                Height: roi.Geometry.Height,
                AngleDegrees: roi.Geometry.Angle,
                SearchMarginPixels: roi.SearchRange?.Horizontal ?? 0.0,
                PrimaryFilter: roi.Filter,
                FilterPipeline: [.. roi.FilterPipeline],
                OcvExpectedText: roi.Ocv.ExpectedText,
                OcvFormatMask: roi.Ocv.FormatMask));
        }

        var doc = new TeachingRecipeDocument
        {
            ActiveTeachingCategory = Inspections.Selected.ToString(),
            PreFilterBeforeInspection = Filters.PreFilterBeforeInspection,
            SessionFilterPipeline = [.. Filters.PipelineSteps.Select(static s => s.Settings)],
            Rois = rois
        };

        var recipePath = Path.Combine(AppContext.BaseDirectory, "latest-teaching-recipe.json");
        File.WriteAllText(recipePath, doc.ToJson());
        Status = $"티칭 레시피 저장 완료: ROI {rois.Count}개 · 필터 파이프라인 {doc.SessionFilterPipeline.Count}단계 ({Path.GetFileName(recipePath)})";
    }

    public void LoadTeachingRecipeFromDefaultFile()
    {
        var recipePath = Path.Combine(AppContext.BaseDirectory, "latest-teaching-recipe.json");
        if (!File.Exists(recipePath))
        {
            Status = "저장된 티칭 레시피 파일(latest-teaching-recipe.json)이 없습니다. 먼저 '레시피 저장'을 실행하세요.";
            return;
        }

        var json = File.ReadAllText(recipePath);
        var doc = TeachingRecipeDocument.FromJson(json);
        if (doc is null)
        {
            Status = "티칭 레시피 JSON 파싱에 실패했습니다.";
            return;
        }

        Filters.LoadRecipePipeline(doc.SessionFilterPipeline, doc.PreFilterBeforeInspection);
        if (Enum.TryParse<InspectionToolKind>(doc.ActiveTeachingCategory, out var category))
        {
            Inspections.Selected = category;
        }

        Status = $"티칭 레시피 불러오기 완료: 카테고리={doc.ActiveTeachingCategory} · ROI {doc.Rois.Count}개 · 필터 {doc.SessionFilterPipeline.Count}단계";
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
        Filters.ActivePipelineChanged -= SyncPreInspectionFilterPipeline;
        Inspections.Changed -= Publish;
        Inspections.AssignmentsChanged -= RefreshRowAssignments;
        Inspections.LocateBoundsRequested -= LocateInspection;
        Matching.LocateRequested -= LocateInspection;
        Matching.SearchRangeRequested -= EnsurePatternSearchRange;
        Inspections.Shapes.LocateRequested -= LocateInspection;
        if (Inspections.FewShot is { } fewShot)
        {
            fewShot.LocateRequested -= LocateInspection;
        }
        if (Inspections.Suite is { } suite)
        {
            suite.LocateRequested -= LocateInspection;
            suite.SampleLoadRequested -= RequestSuiteSampleLoad;
            suite.RoiOcvSettingsChangeRequested -= ApplyRoiOcvSettings;
        }
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
