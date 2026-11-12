using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TeachingToolWpf.Commands;
using TeachingToolWpf.Imaging;
using TeachingToolWpf.Imaging.Matching;
using TeachingToolWpf.Infrastructure;
using TeachingToolWpf.Models;
using VisionAlgorithm.OpenSrc.PatMax;
using VisionCanvas.Core.Editing;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.ViewModels;

/// <summary>
/// 기존 VisionCanvas와 분리된 전용 패턴 마스크 페이지 ViewModel.
/// 선택된 ROI의 회전 보정된(Rectified) 기준 패턴 영상과 경계 특징점(에지 다이폴)을 추출하고,
/// 로컬 ROI 좌표계에서 사각형/타원 벡터 도형 및 2D 래스터 브러시/지우개(RLE 자동 병합)로 제외 마스크를 60FPS 이상으로 편집해 EditorSession에 반영한다.
/// </summary>
public sealed class PatternMaskEditorViewModel : ObservableObject
{
    private const double MinMaskNormalizedSpan = 0.005;
    private const double DefaultEdgeThreshold = 18.0;
    private const int MaxPreviewDimension = 512;
    private const int MaxOverlayFeatureMarkers = 600;
    private const int MaxReaderPixelsBudget = 67_108_864;
    private const int ReaderWindowBudgetPixels = VisionAlgorithm.OpenSrc.PatternMatching.PreparedSearchImage.MaximumPixels;
    private const double PixelCenterOffset = 0.5;

    private readonly record struct CachedEdgeDipole(
        double LocalX,
        double LocalY,
        float CosTheta,
        float SinTheta,
        PointD NormalizedPoint,
        int PreviewX,
        int PreviewY,
        bool IsSubsampledMarker);

    private readonly EditorSession _session;
    private readonly IImageLoader _loader;
    private Func<VideoFrame?> _retainFrame;
    private readonly Action<string>? _statusCallback;
    private readonly IPreInspectionFilterPipeline? _preFilterPipeline;

    private Guid? _roiId;
    private string _roiName = "";
    private RoiGeometry? _roiGeometry;
    private byte[]? _rectifiedPixels;
    private byte[]? _shapeInsideMask;
    private int _previewWidth;
    private int _previewHeight;
    private WriteableBitmap? _previewBitmap;

    // 2D raster brush/polygon mask layer (255 = masked, 0 = unmasked) & incremental BGRA overlay bitmap
    private byte[]? _brushMaskGrid;
    private byte[]? _brushOverlayBgra;
    private WriteableBitmap? _brushMaskBitmap;
    private int _maskedBrushPixelCount;
    private int _brushStrokeSegmentCount;
    private MaskRegion? _singleStampEllipse;
    private bool _isInteractiveStrokeActive;

    // Pre-extracted edge dipoles (extracted only when ROI image or EdgeThreshold changes, never on MouseMove)
    private CachedEdgeDipole[] _cachedDipoles = [];
    private PatternMaskFeaturePoint[] _featurePoints = [];
    private int _activeFeatureCount;
    private int _excludedFeatureCount;

    // Vector masks (Rectangle / Ellipse) + RLE-compiled brush/polygon masks -> combined into _draftMasks
    private readonly List<MaskRegion> _vectorMasks = [];
    private readonly List<MaskRegion> _brushCompiledMasks = [];
    private readonly List<MaskRegion> _draftMasks = [];
    private readonly List<PointD> _draftPolygonPoints = [];

    private int _selectedIndex = -1;
    private PatternMaskDrawTool _activeTool = PatternMaskDrawTool.Rectangle;
    private double _brushSizePixels = 10.0;
    private double _edgeThreshold = DefaultEdgeThreshold;
    private bool _showExcludedOverlay = true;
    private bool _showEdgeFeatures = true;
    private double _zoomScale = 2.5;
    private string _x = "0";
    private string _y = "0";
    private string _width = "0";
    private string _height = "0";
    private string _status = "선택된 ROI의 패턴 영상을 불러오는 중…";
    private string _error = "";
    private bool _isHelpManualOpen;

    public PatternMaskEditorViewModel(
        EditorSession session,
        IImageLoader loader,
        Func<VideoFrame?> retainFrame,
        Action<string>? statusCallback = null,
        IPreInspectionFilterPipeline? preFilterPipeline = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(loader);
        ArgumentNullException.ThrowIfNull(retainFrame);

        _session = session;
        _loader = loader;
        _retainFrame = retainFrame;
        _statusCallback = statusCallback;
        _preFilterPipeline = preFilterPipeline;

        SelectToolCommand = new(() => ActiveTool = PatternMaskDrawTool.Select);
        RectangleToolCommand = new(() => ActiveTool = PatternMaskDrawTool.Rectangle);
        EllipseToolCommand = new(() => ActiveTool = PatternMaskDrawTool.Ellipse);
        PolygonToolCommand = new(() => ActiveTool = PatternMaskDrawTool.Polygon);
        BrushToolCommand = new(() => ActiveTool = PatternMaskDrawTool.Brush);
        EraserToolCommand = new(() => ActiveTool = PatternMaskDrawTool.Eraser);

        RemoveSelectedCommand = new(RemoveSelectedMask, () => HasSelectedMask);
        ClearAllCommand = new(ClearAllMasks, () => _draftMasks.Count > 0 || HasAnyBrushPixels() || _draftPolygonPoints.Count > 0);
        ResetToSavedCommand = new(ResetToSavedMasks, () => HasTargetRoi);
        ApplyNumericBoundsCommand = new(ApplyNumericBounds, () => HasSelectedMask);
        CommitToRoiCommand = new(() => _ = CommitMasksToSession(), () => HasTargetRoi);
        ZoomInCommand = new(() => ZoomScale = Math.Clamp(ZoomScale * 1.25, 0.5, 12.0));
        ZoomOutCommand = new(() => ZoomScale = Math.Clamp(ZoomScale / 1.25, 0.5, 12.0));
        ZoomFitCommand = new(() => ZoomScale = 2.5);
        ToggleHelpManualCommand = new(() => IsHelpManualOpen = !IsHelpManualOpen);
    }

    public void AttachFrameRetainer(Func<VideoFrame?> retainFrame)
    {
        ArgumentNullException.ThrowIfNull(retainFrame);
        _retainFrame = retainFrame;
    }

    public event Action? VisualStateChanged;

    public RelayCommand SelectToolCommand { get; }
    public RelayCommand RectangleToolCommand { get; }
    public RelayCommand EllipseToolCommand { get; }
    public RelayCommand PolygonToolCommand { get; }
    public RelayCommand BrushToolCommand { get; }
    public RelayCommand EraserToolCommand { get; }
    public RelayCommand RemoveSelectedCommand { get; }
    public RelayCommand ClearAllCommand { get; }
    public RelayCommand ResetToSavedCommand { get; }
    public RelayCommand ApplyNumericBoundsCommand { get; }
    public RelayCommand CommitToRoiCommand { get; }
    public RelayCommand ZoomInCommand { get; }
    public RelayCommand ZoomOutCommand { get; }
    public RelayCommand ZoomFitCommand { get; }
    public RelayCommand ToggleHelpManualCommand { get; }

    public bool IsHelpManualOpen
    {
        get => _isHelpManualOpen;
        set => Set(ref _isHelpManualOpen, value);
    }

    public AlgorithmHelpManual ActiveHelpManual { get; } = new(
        Title: "패턴 마스크 전용 편집기 · RLE 브러시 & 벡터 도형 마스킹",
        Subtitle: "Rectified ROI Edge Dipole Filtering & Run-Length Encoded Exclusion Mask",
        PrincipleAndFormula: "선택된 ROI를 회전 보정(Rectified)한 로컬 좌표계에서 Sobel 에지 다이폴(p_i, n_i)을 추출한 뒤, 사각형·타원·폴리곤 벡터 도형 또는 2D 래스터 브러시(RLE 자동 병합)로 칠한 영역 내부의 특징점을 패턴 학습 및 매칭 점수 계산에서 완전히 제외합니다.",
        StepByStepGuide: "1. 상단 도구바에서 사각형·타원·폴리곤·브러시·지우개 중 원하는 도구를 선택합니다.\n2. 캔버스에서 변동이 심한 영역(로트 번호, 배경 노이즈, 반사광 등)을 드래그해 마스킹합니다.\n3. 초록색(유효 특징점)과 빨간색(제외 특징점) 분포를 확인한 뒤 [ROI에 마스크 적용]을 누릅니다.",
        ParameterTuningGuide: "• 브러시 크기(3~64 px): 자유 곡선 마스킹 두께를 조절합니다.\n• 에지 민감도(5~80): 미리보기 에지 특징점 추출 임계값을 조절합니다.",
        TroubleshootingTips: "• 대칭 구조나 가변 각인이 포함된 부품은 해당 영역을 마스킹하면 오검출과 점수 저하를 동시에 방지할 수 있습니다.");

    public ObservableCollection<MaskRow> Rows { get; } = [];
    public PatternMaskFeaturePoint[] FeaturePoints => _featurePoints;

    public bool HasTargetRoi => _roiId is not null && _roiGeometry is not null;
    public bool HasSelectedMask => _selectedIndex >= 0 && _selectedIndex < _draftMasks.Count;
    public bool HasDraftPolygon => _draftPolygonPoints.Count > 0;
    public int MaskCount => _draftMasks.Count;
    public int VectorMaskCount => _vectorMasks.Count;
    public int ActiveFeatureCount => _activeFeatureCount;
    public int ExcludedFeatureCount => _excludedFeatureCount;

    public Guid? TargetRoiId => _roiId;
    public RoiGeometry? TargetGeometry => _roiGeometry;
    public int PreviewWidth => _previewWidth;
    public int PreviewHeight => _previewHeight;
    public double RoiWidth => _roiGeometry?.Width ?? 1.0;
    public double RoiHeight => _roiGeometry?.Height ?? 1.0;
    public double CanvasDisplayWidth => Math.Max(64.0, RoiWidth * _zoomScale);
    public double CanvasDisplayHeight => Math.Max(64.0, RoiHeight * _zoomScale);

    public ImageSource? PreviewImage => _previewBitmap;
    public ImageSource? BrushMaskImage => _showExcludedOverlay ? _brushMaskBitmap : null;

    public IReadOnlyList<MaskRegion> DraftMasks => _draftMasks;
    public IReadOnlyList<MaskRegion> VectorMasks => _vectorMasks;
    public IReadOnlyList<PointD> DraftPolygonPoints => _draftPolygonPoints;

    public string HeaderTitle => HasTargetRoi
        ? $"패턴 마스크 전용 편집기 · {_roiName} ({RoiWidth:0.#} × {RoiHeight:0.#} px, 회전 {_roiGeometry!.Angle:0.#}°)"
        : "패턴 마스크 전용 편집기 · 선택된 단일 ROI 없음";

    public string FeatureSummary => HasTargetRoi
        ? $"유효 특징점 {ActiveFeatureCount:N0}개 · 마스크 제외 {ExcludedFeatureCount:N0}개 · 마스크 영역 {MaskCount}개"
        : "캔버스 또는 ROI 목록에서 검사 ROI 하나를 선택하세요.";

    public PatternMaskDrawTool ActiveTool
    {
        get => _activeTool;
        set
        {
            if (_activeTool != value && _activeTool == PatternMaskDrawTool.Polygon && _draftPolygonPoints.Count > 0)
            {
                if (_draftPolygonPoints.Count >= 3)
                {
                    TryCompleteDraftPolygon();
                }
                else
                {
                    _draftPolygonPoints.Clear();
                }
            }

            if (Set(ref _activeTool, value))
            {
                Notify(nameof(IsSelectTool));
                Notify(nameof(IsRectangleTool));
                Notify(nameof(IsEllipseTool));
                Notify(nameof(IsPolygonTool));
                Notify(nameof(IsBrushTool));
                Notify(nameof(IsEraserTool));
                Notify(nameof(ToolGuideText));
                VisualStateChanged?.Invoke();
            }
        }
    }

    public bool IsSelectTool => _activeTool == PatternMaskDrawTool.Select;
    public bool IsRectangleTool => _activeTool == PatternMaskDrawTool.Rectangle;
    public bool IsEllipseTool => _activeTool == PatternMaskDrawTool.Ellipse;
    public bool IsPolygonTool => _activeTool == PatternMaskDrawTool.Polygon;
    public bool IsBrushTool => _activeTool == PatternMaskDrawTool.Brush;
    public bool IsEraserTool => _activeTool == PatternMaskDrawTool.Eraser;

    public string ToolGuideText => _activeTool switch
    {
        PatternMaskDrawTool.Select => "선택 모드: 마스크 도형을 클릭해 선택하거나 드래그하여 위치를 이동하세요.",
        PatternMaskDrawTool.Rectangle => "사각형 마스크: 패턴 위에서 드래그하여 직사각형 제외 영역을 추가합니다.",
        PatternMaskDrawTool.Ellipse => "타원 마스크: 패턴 위에서 드래그하여 원형/타원 제외 영역을 추가합니다.",
        PatternMaskDrawTool.Polygon => "폴리곤 마스크: 클릭해 꼭짓점을 찍고 시작점 클릭·더블클릭·우클릭·Enter로 완성합니다. (Backspace: 이전 점 취소, Esc: 전체 취소)",
        PatternMaskDrawTool.Brush => "브러시 그리기: 마우스를 누른 채 드래그하여 자유 곡선 형태로 제외 마스크를 제한 없이 칠합니다.",
        PatternMaskDrawTool.Eraser => "마스크 지우개: 클릭하거나 드래그하여 지나가는 위치의 제외 마스크를 즉시 지웁니다.",
        _ => ""
    };

    public double BrushSizePixels
    {
        get => _brushSizePixels;
        set
        {
            if (Set(ref _brushSizePixels, Math.Clamp(value, 2.0, 128.0)))
            {
                VisualStateChanged?.Invoke();
            }
        }
    }

    public double EdgeThreshold
    {
        get => _edgeThreshold;
        set
        {
            var clamped = Math.Clamp(value, 3.0, 120.0);
            if (Set(ref _edgeThreshold, clamped))
            {
                ExtractRawEdgeDipoles();
                UpdateFeatureExclusionStates();
            }
        }
    }

    public bool ShowExcludedOverlay
    {
        get => _showExcludedOverlay;
        set
        {
            if (Set(ref _showExcludedOverlay, value))
            {
                Notify(nameof(BrushMaskImage));
                VisualStateChanged?.Invoke();
            }
        }
    }

    public bool ShowEdgeFeatures
    {
        get => _showEdgeFeatures;
        set
        {
            if (Set(ref _showEdgeFeatures, value))
            {
                VisualStateChanged?.Invoke();
            }
        }
    }

    public double ZoomScale
    {
        get => _zoomScale;
        set
        {
            var clamped = Math.Clamp(value, 0.5, 12.0);
            if (Set(ref _zoomScale, clamped))
            {
                Notify(nameof(ZoomPercentText));
                Notify(nameof(CanvasDisplayWidth));
                Notify(nameof(CanvasDisplayHeight));
                VisualStateChanged?.Invoke();
            }
        }
    }

    public string ZoomPercentText => $"{_zoomScale * 100.0:0}%";

    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            var clamped = value >= 0 && value < _draftMasks.Count ? value : -1;
            if (Set(ref _selectedIndex, clamped))
            {
                SyncNumericFieldsFromSelectedMask();
                Notify(nameof(HasSelectedMask));
                RefreshCommands();
                VisualStateChanged?.Invoke();
            }
        }
    }

    public string X
    {
        get => _x;
        set => Set(ref _x, value);
    }

    public string Y
    {
        get => _y;
        set => Set(ref _y, value);
    }

    public string Width
    {
        get => _width;
        set => Set(ref _width, value);
    }

    public string Height
    {
        get => _height;
        set => Set(ref _height, value);
    }

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public string Error
    {
        get => _error;
        private set => Set(ref _error, value);
    }

    /// <summary>
    /// 마우스 왼쪽 버튼을 누를 때 호출하여 드래그 중 불필요한 RLE 병합 및 ListBox 재구성을 유예한다.
    /// </summary>
    public void BeginInteractiveStroke()
    {
        _isInteractiveStrokeActive = true;
    }

    /// <summary>
    /// 마우스 왼쪽 버튼을 뗄 때 호출하여 2D 브러시 마스크 그리드를 최적의 RLE 직사각형 집합으로 병합하고 목록을 1회 갱신한다.
    /// </summary>
    public void EndInteractiveStroke()
    {
        if (!_isInteractiveStrokeActive)
        {
            return;
        }

        _isInteractiveStrokeActive = false;
        SynchronizeCompiledBrushMasks();
        RebuildMaskRows(selectIndex: _draftMasks.Count - 1);
        UpdateFeatureExclusionStates();
        Status = $"마스크 편집 반영 · 총 마스크 영역 {_draftMasks.Count}개 (미저장 초안)";
    }

    public async Task LoadSelectedRoiAsync(ImageReference? image, bool live, CancellationToken token = default)
    {
        var state = _session.State;
        if (state.SelectionCount != 1 || state.Selection.Count != 1)
        {
            ClearTarget("단일 리프 ROI 하나를 선택한 뒤 패턴 마스크 편집기를 여세요.");
            return;
        }

        var id = state.Selection.Single();
        if (state.IsGroupRoi(id) || !state.Rois.TryGetValue(id, out var roi))
        {
            ClearTarget("그룹 ROI가 아닌 일반 검사 ROI 하나를 선택하세요.");
            return;
        }

        _roiId = roi.Id;
        _roiName = roi.Name;
        _roiGeometry = roi.Geometry;

        var targetImage = image ?? state.Image;
        if (targetImage is null)
        {
            ClearTarget("열린 영상이 없습니다.");
            return;
        }

        try
        {
            var (pixels, shapeMask, w, h) = await Task.Run(
                () => ExtractRectifiedRoiPatchAsync(targetImage, live, roi.Geometry, token),
                token);

            _rectifiedPixels = pixels;
            _shapeInsideMask = shapeMask;
            _previewWidth = w;
            _previewHeight = h;

            var maxDim = Math.Max(roi.Geometry.Width, roi.Geometry.Height);
            _zoomScale = Math.Clamp(380.0 / Math.Max(16.0, maxDim), 1.0, 8.0);

            InitializeBrushMaskLayer(w, h);
            _vectorMasks.Clear();
            _vectorMasks.AddRange(roi.Masks);
            SynchronizeCompiledBrushMasks();
            RebuildMaskRows(selectIndex: _draftMasks.Count - 1);

            RebuildPreviewBitmap();
            ExtractRawEdgeDipoles();
            UpdateFeatureExclusionStates();

            Error = "";
            Status = $"{_roiName} 패턴 영상 로드 완료 · 마우스로 마스크를 그리거나 편집한 뒤 ‘ROI에 마스크 적용’을 누르세요.";
            NotifyAllProperties();
            VisualStateChanged?.Invoke();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Error = ex.Message;
            Status = "패턴 영상 추출 실패: " + ex.Message;
            NotifyAllProperties();
        }
    }

    /// <summary>
    /// 메모리 상의 그레이스케일 버퍼로 직접 초기화한다 (단위 테스트 및 오프라인 편집용).
    /// </summary>
    public void LoadFromMemoryPatch(
        Guid roiId,
        string roiName,
        RoiGeometry geometry,
        ReadOnlySpan<byte> rectifiedGrayPixels,
        int width,
        int height,
        ImmutableArray<MaskRegion> initialMasks)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (rectifiedGrayPixels.Length < checked(width * height))
        {
            throw new ArgumentException("rectifiedGrayPixels buffer is smaller than width * height.", nameof(rectifiedGrayPixels));
        }

        _roiId = roiId;
        _roiName = roiName;
        _roiGeometry = geometry;
        _previewWidth = width;
        _previewHeight = height;
        _rectifiedPixels = rectifiedGrayPixels.Slice(0, width * height).ToArray();
        _shapeInsideMask = BuildLocalShapeInsideMask(geometry, width, height);

        InitializeBrushMaskLayer(width, height);
        _vectorMasks.Clear();
        if (!initialMasks.IsDefaultOrEmpty)
        {
            _vectorMasks.AddRange(initialMasks);
        }

        SynchronizeCompiledBrushMasks();
        RebuildMaskRows(selectIndex: _draftMasks.Count - 1);
        RebuildPreviewBitmap();
        ExtractRawEdgeDipoles();
        UpdateFeatureExclusionStates();

        Error = "";
        Status = $"{_roiName} 패턴 로드 완료";
        NotifyAllProperties();
        VisualStateChanged?.Invoke();
    }

    public bool TryAddMaskFromLocalPixels(MaskShape shape, double x0, double y0, double x1, double y1)
    {
        if (!HasTargetRoi)
        {
            return false;
        }

        if (_vectorMasks.Count >= EditorSession.MaximumMasksPerRoi)
        {
            Error = $"ROI 하나에 마스크는 최대 {EditorSession.MaximumMasksPerRoi}개까지 추가할 수 있습니다.";
            return false;
        }

        var w = RoiWidth;
        var h = RoiHeight;
        var left = Math.Clamp(Math.Min(x0, x1) / w, 0.0, 1.0);
        var top = Math.Clamp(Math.Min(y0, y1) / h, 0.0, 1.0);
        var right = Math.Clamp(Math.Max(x0, x1) / w, 0.0, 1.0);
        var bottom = Math.Clamp(Math.Max(y0, y1) / h, 0.0, 1.0);

        if ((right - left) < MinMaskNormalizedSpan || (bottom - top) < MinMaskNormalizedSpan)
        {
            return false;
        }

        var region = new MaskRegion(shape, new BoundsD(left, top, right, bottom));
        _vectorMasks.Add(region);
        SynchronizeCompiledBrushMasks();
        Error = "";
        RebuildMaskRows(selectIndex: _vectorMasks.Count - 1);
        UpdateFeatureExclusionStates();
        Status = $"{(shape == MaskShape.Rectangle ? "사각형" : "타원")} 마스크 #{_draftMasks.Count} 추가됨 (미저장 초안)";
        return true;
    }

    public void AddDraftPolygonPoint(double localX, double localY)
    {
        if (!HasTargetRoi)
        {
            return;
        }

        double clampedX = Math.Clamp(localX, 0.0, RoiWidth);
        double clampedY = Math.Clamp(localY, 0.0, RoiHeight);

        if (_draftPolygonPoints.Count > 0)
        {
            var last = _draftPolygonPoints[^1];
            double dx = clampedX - last.X;
            double dy = clampedY - last.Y;
            if ((dx * dx) + (dy * dy) < 0.25)
            {
                return;
            }
        }

        _draftPolygonPoints.Add(new PointD(clampedX, clampedY));
        Error = "";
        Notify(nameof(HasDraftPolygon));
        RefreshCommands();
        Status = _draftPolygonPoints.Count < 3
            ? $"폴리곤 꼭짓점 {_draftPolygonPoints.Count}개 지정됨 · 최소 3개 이상의 점을 찍으세요."
            : $"폴리곤 꼭짓점 {_draftPolygonPoints.Count}개 지정됨 · 시작점 클릭, 더블클릭, 우클릭 또는 Enter로 완성 (Esc: 취소)";
        VisualStateChanged?.Invoke();
    }

    public bool IsNearDraftPolygonStart(double localX, double localY, double snapRadiusLocalPixels)
    {
        if (_draftPolygonPoints.Count < 3)
        {
            return false;
        }

        var start = _draftPolygonPoints[0];
        double dx = localX - start.X;
        double dy = localY - start.Y;
        return (dx * dx) + (dy * dy) <= snapRadiusLocalPixels * snapRadiusLocalPixels;
    }

    public bool UndoLastDraftPolygonPoint()
    {
        if (_draftPolygonPoints.Count == 0)
        {
            return false;
        }

        _draftPolygonPoints.RemoveAt(_draftPolygonPoints.Count - 1);
        Notify(nameof(HasDraftPolygon));
        RefreshCommands();
        Status = _draftPolygonPoints.Count == 0
            ? "폴리곤 입력이 취소되었습니다."
            : $"폴리곤 마지막 꼭짓점 취소됨 (남은 꼭짓점 {_draftPolygonPoints.Count}개)";
        VisualStateChanged?.Invoke();
        return true;
    }

    public void CancelDraftPolygon()
    {
        if (_draftPolygonPoints.Count == 0)
        {
            return;
        }

        _draftPolygonPoints.Clear();
        Notify(nameof(HasDraftPolygon));
        RefreshCommands();
        Status = "그리던 폴리곤 마스크를 취소했습니다.";
        VisualStateChanged?.Invoke();
    }

    public bool TryCompleteDraftPolygon()
    {
        if (_draftPolygonPoints.Count < 3)
        {
            Error = "폴리곤 마스크는 최소 3개 이상의 꼭짓점이 필요합니다.";
            return false;
        }

        var vertices = _draftPolygonPoints.ToArray();
        _draftPolygonPoints.Clear();
        Notify(nameof(HasDraftPolygon));
        RefreshCommands();
        return TryAddPolygonMaskFromLocalPixels(vertices);
    }

    /// <summary>
    /// 다각형(Polygon) 꼭짓점 배열(로컬 ROI 픽셀 좌표)을 스캔라인 채우기로 2D 래스터 마스크 그리드에 즉시 반영한다.
    /// </summary>
    public bool TryAddPolygonMaskFromLocalPixels(IReadOnlyList<PointD> localVertices)
    {
        if (!HasTargetRoi || localVertices is null || localVertices.Count < 3)
        {
            return false;
        }

        EnsureBrushMaskLayerReady();
        _singleStampEllipse = null;
        _brushStrokeSegmentCount++;

        if (!RasterizePolygonOnBrushGrid(localVertices, byte.MaxValue, out int minX, out int minY, out int maxX, out int maxY))
        {
            return false;
        }

        UpdateBrushOverlayBitmapRegion(minX, minY, maxX, maxY);
        Error = "";
        SynchronizeCompiledBrushMasks();
        RebuildMaskRows(selectIndex: _draftMasks.Count - 1);
        UpdateFeatureExclusionStates();
        Status = $"폴리곤 마스크(꼭짓점 {localVertices.Count}개) 추가됨 · 총 마스크 영역 {_draftMasks.Count}개 (미저장 초안)";
        return true;
    }

    public bool TryStampBrushAtLocalPixel(double centerX, double centerY)
        => TryPaintBrushStrokeSegment(centerX, centerY, centerX, centerY);

    /// <summary>
    /// 2D 래스터 브러시 레이어에 캡슐 선분 (x0, y0) -> (x1, y1)을 직접 칠한다.
    /// 픽셀 그리드 기반이므로 아무리 오래 칠해도 개수 제한에 걸리지 않으며 O(dirtyRect) 시간에 즉시 갱신된다.
    /// </summary>
    public bool TryPaintBrushStrokeSegment(double x0, double y0, double x1, double y1)
    {
        if (!HasTargetRoi)
        {
            return false;
        }

        EnsureBrushMaskLayerReady();

        var radiusLocal = Math.Max(1.0, _brushSizePixels * 0.5);
        var w = Math.Max(1.0, RoiWidth);
        var h = Math.Max(1.0, RoiHeight);

        bool isSinglePoint = Math.Abs(x1 - x0) <= 1e-4 && Math.Abs(y1 - y0) <= 1e-4;
        if (isSinglePoint && _brushStrokeSegmentCount == 0 && !HasAnyBrushPixels())
        {
            var halfNormW = Math.Max(MinMaskNormalizedSpan * 0.5, radiusLocal / w);
            var halfNormH = Math.Max(MinMaskNormalizedSpan * 0.5, radiusLocal / h);
            var normCx = Math.Clamp(x0 / w, halfNormW, 1.0 - halfNormW);
            var normCy = Math.Clamp(y0 / h, halfNormH, 1.0 - halfNormH);
            _singleStampEllipse = new MaskRegion(
                MaskShape.Ellipse,
                new BoundsD(
                    Math.Clamp(normCx - halfNormW, 0.0, 1.0),
                    Math.Clamp(normCy - halfNormH, 0.0, 1.0),
                    Math.Clamp(normCx + halfNormW, 0.0, 1.0),
                    Math.Clamp(normCy + halfNormH, 0.0, 1.0)));
        }
        else
        {
            _singleStampEllipse = null;
        }

        _brushStrokeSegmentCount++;

        RasterizeCapsuleOnBrushGrid(
            x0,
            y0,
            x1,
            y1,
            radiusLocal,
            paintValue: byte.MaxValue,
            out int minX,
            out int minY,
            out int maxX,
            out int maxY);
        UpdateBrushOverlayBitmapRegion(minX, minY, maxX, maxY);

        Error = "";
        if (!_isInteractiveStrokeActive)
        {
            SynchronizeCompiledBrushMasks();
            RebuildMaskRows(selectIndex: _draftMasks.Count - 1);
            Status = $"브러시 그리기 적용 · 총 마스크 영역 {_draftMasks.Count}개 (미저장 초안)";
        }

        UpdateFeatureExclusionStates();
        return true;
    }

    public bool TryEraseMasksAlongSegment(double x0, double y0, double x1, double y1)
    {
        if (!HasTargetRoi)
        {
            return false;
        }

        EnsureBrushMaskLayerReady();

        var eraseRadiusLocal = Math.Max(1.5, _brushSizePixels * 0.55);
        int dirtyMinX = _previewWidth;
        int dirtyMinY = _previewHeight;
        int dirtyMaxX = -1;
        int dirtyMaxY = -1;
        var convertedVectorMask = false;

        // If any vector mask (Rectangle/Ellipse) is touched by the eraser capsule, promote it into the 2D raster grid first
        // so the eraser smoothly carves a circular path through any mask type at 60+ FPS.
        if (_vectorMasks.Count > 0)
        {
            double segMinNormX = (Math.Min(x0, x1) - eraseRadiusLocal) / Math.Max(1.0, RoiWidth);
            double segMaxNormX = (Math.Max(x0, x1) + eraseRadiusLocal) / Math.Max(1.0, RoiWidth);
            double segMinNormY = (Math.Min(y0, y1) - eraseRadiusLocal) / Math.Max(1.0, RoiHeight);
            double segMaxNormY = (Math.Max(y0, y1) + eraseRadiusLocal) / Math.Max(1.0, RoiHeight);

            for (var i = _vectorMasks.Count - 1; i >= 0; i--)
            {
                var vm = _vectorMasks[i];
                var b = vm.Bounds;
                if (b.Right < segMinNormX || b.Left > segMaxNormX || b.Bottom < segMinNormY || b.Top > segMaxNormY)
                {
                    continue;
                }

                if (DoesCapsuleTouchVectorMask(vm, x0, y0, x1, y1, eraseRadiusLocal))
                {
                    RasterizeVectorMaskIntoBrushGrid(vm, out int vMinX, out int vMinY, out int vMaxX, out int vMaxY);
                    _vectorMasks.RemoveAt(i);
                    convertedVectorMask = true;
                    dirtyMinX = Math.Min(dirtyMinX, vMinX);
                    dirtyMinY = Math.Min(dirtyMinY, vMinY);
                    dirtyMaxX = Math.Max(dirtyMaxX, vMaxX);
                    dirtyMaxY = Math.Max(dirtyMaxY, vMaxY);
                }
            }
        }

        var erasedBrushPixels = RasterizeCapsuleOnBrushGrid(
            x0,
            y0,
            x1,
            y1,
            eraseRadiusLocal,
            paintValue: 0,
            out int cMinX,
            out int cMinY,
            out int cMaxX,
            out int cMaxY);

        if (erasedBrushPixels || convertedVectorMask)
        {
            _singleStampEllipse = null;
            if (!HasAnyBrushPixels())
            {
                _brushStrokeSegmentCount = 0;
            }

            dirtyMinX = Math.Min(dirtyMinX, cMinX);
            dirtyMinY = Math.Min(dirtyMinY, cMinY);
            dirtyMaxX = Math.Max(dirtyMaxX, cMaxX);
            dirtyMaxY = Math.Max(dirtyMaxY, cMaxY);
            UpdateBrushOverlayBitmapRegion(dirtyMinX, dirtyMinY, dirtyMaxX, dirtyMaxY);

            Error = "";
            if (!_isInteractiveStrokeActive)
            {
                SynchronizeCompiledBrushMasks();
                RebuildMaskRows(selectIndex: Math.Min(_selectedIndex, _draftMasks.Count - 1));
                Status = $"지우개로 마스크 삭제됨 · 남은 마스크 영역 {_draftMasks.Count}개";
            }

            UpdateFeatureExclusionStates();
            return true;
        }

        return false;
    }

    public bool TryEraseMasksAtLocalPixel(double localX, double localY)
        => TryEraseMasksAlongSegment(localX, localY, localX, localY);

    public int HitTestMaskAtLocalPixel(double localX, double localY)
    {
        if (!HasTargetRoi)
        {
            return -1;
        }

        if (_isInteractiveStrokeActive)
        {
            SynchronizeCompiledBrushMasks();
        }

        if (_draftMasks.Count == 0)
        {
            return -1;
        }

        var normPoint = new PointD(
            Math.Clamp(localX / RoiWidth, 0.0, 1.0),
            Math.Clamp(localY / RoiHeight, 0.0, 1.0));

        for (var i = _draftMasks.Count - 1; i >= 0; i--)
        {
            if (_draftMasks[i].ContainsNormalized(normPoint))
            {
                return i;
            }
        }

        return -1;
    }

    public bool TryTranslateSelectedMaskByLocalPixels(double deltaX, double deltaY)
    {
        if (!HasTargetRoi || !HasSelectedMask)
        {
            return false;
        }

        var current = _draftMasks[_selectedIndex];
        var bounds = current.Bounds;
        var normDx = deltaX / RoiWidth;
        var normDy = deltaY / RoiHeight;

        var clampedDx = Math.Clamp(normDx, -bounds.Left, 1.0 - bounds.Right);
        var clampedDy = Math.Clamp(normDy, -bounds.Top, 1.0 - bounds.Bottom);
        if (Math.Abs(clampedDx) <= 1e-9 && Math.Abs(clampedDy) <= 1e-9)
        {
            return false;
        }

        var movedBounds = new BoundsD(
            bounds.Left + clampedDx,
            bounds.Top + clampedDy,
            bounds.Right + clampedDx,
            bounds.Bottom + clampedDy);
        var movedRegion = new MaskRegion(current.Shape, movedBounds);

        if (_selectedIndex < _vectorMasks.Count)
        {
            _vectorMasks[_selectedIndex] = movedRegion;
            _draftMasks[_selectedIndex] = movedRegion;
        }
        else
        {
            // Promote selected RLE brush region to a movable vector mask and clear its old bounds from the brush grid
            FillNormalizedBoundsOnBrushGrid(bounds, 0);
            _singleStampEllipse = null;
            _vectorMasks.Add(movedRegion);
            SynchronizeCompiledBrushMasks();
            _selectedIndex = _vectorMasks.Count - 1;
            UpdateBrushOverlayBitmapRegion(0, 0, _previewWidth - 1, _previewHeight - 1);
        }

        SyncNumericFieldsFromSelectedMask();
        UpdateFeatureExclusionStates();
        return true;
    }

    public void RemoveSelectedMask()
    {
        if (!HasSelectedMask)
        {
            return;
        }

        if (_selectedIndex < _vectorMasks.Count)
        {
            _vectorMasks.RemoveAt(_selectedIndex);
        }
        else
        {
            var targetBounds = _draftMasks[_selectedIndex].Bounds;
            FillNormalizedBoundsOnBrushGrid(targetBounds, 0);
            _singleStampEllipse = null;
            if (!HasAnyBrushPixels())
            {
                _brushStrokeSegmentCount = 0;
            }

            UpdateBrushOverlayBitmapRegion(0, 0, _previewWidth - 1, _previewHeight - 1);
        }

        SynchronizeCompiledBrushMasks();
        Error = "";
        RebuildMaskRows(selectIndex: Math.Min(_selectedIndex, _draftMasks.Count - 1));
        UpdateFeatureExclusionStates();
        Status = $"선택 마스크 삭제됨 · 남은 마스크 {_draftMasks.Count}개";
    }

    public void ClearAllMasks()
    {
        if (_draftMasks.Count == 0 && !HasAnyBrushPixels() && _draftPolygonPoints.Count == 0)
        {
            return;
        }

        _vectorMasks.Clear();
        _brushCompiledMasks.Clear();
        _draftMasks.Clear();
        _draftPolygonPoints.Clear();
        _singleStampEllipse = null;
        _brushStrokeSegmentCount = 0;
        _maskedBrushPixelCount = 0;

        if (_brushMaskGrid is not null)
        {
            Array.Clear(_brushMaskGrid);
            UpdateBrushOverlayBitmapRegion(0, 0, _previewWidth - 1, _previewHeight - 1);
        }

        Error = "";
        Notify(nameof(HasDraftPolygon));
        RebuildMaskRows(selectIndex: -1);
        UpdateFeatureExclusionStates();
        Status = "모든 초안 마스크를 비웠습니다. ‘ROI에 마스크 적용’을 누르면 확정됩니다.";
    }

    public void ResetToSavedMasks()
    {
        if (_roiId is not { } id || !_session.State.Rois.TryGetValue(id, out var roi))
        {
            return;
        }

        _vectorMasks.Clear();
        _vectorMasks.AddRange(roi.Masks);
        _draftPolygonPoints.Clear();
        _singleStampEllipse = null;
        _brushStrokeSegmentCount = 0;
        _maskedBrushPixelCount = 0;

        if (_brushMaskGrid is not null)
        {
            Array.Clear(_brushMaskGrid);
            UpdateBrushOverlayBitmapRegion(0, 0, _previewWidth - 1, _previewHeight - 1);
        }

        Notify(nameof(HasDraftPolygon));
        SynchronizeCompiledBrushMasks();
        Error = "";
        RebuildMaskRows(selectIndex: _draftMasks.Count - 1);
        UpdateFeatureExclusionStates();
        Status = "현재 ROI에 저장된 마스크 상태로 되돌렸습니다.";
    }

    public void ApplyNumericBounds()
    {
        if (!HasTargetRoi || !HasSelectedMask)
        {
            return;
        }

        try
        {
            var current = _draftMasks[_selectedIndex];
            var bounds = current.Bounds;
            var left = ParseNormalized(X, bounds.Left, RoiWidth);
            var top = ParseNormalized(Y, bounds.Top, RoiHeight);
            var width = ParseNormalized(Width, bounds.Width, RoiWidth);
            var height = ParseNormalized(Height, bounds.Height, RoiHeight);
            var right = left == bounds.Left && width == bounds.Width ? bounds.Right : left + width;
            var bottom = top == bounds.Top && height == bounds.Height ? bounds.Bottom : top + height;

            var updated = new MaskRegion(current.Shape, new BoundsD(left, top, right, bottom));
            if (_selectedIndex < _vectorMasks.Count)
            {
                _vectorMasks[_selectedIndex] = updated;
                _draftMasks[_selectedIndex] = updated;
            }
            else
            {
                FillNormalizedBoundsOnBrushGrid(bounds, 0);
                _singleStampEllipse = null;
                _vectorMasks.Add(updated);
                SynchronizeCompiledBrushMasks();
                _selectedIndex = _vectorMasks.Count - 1;
                UpdateBrushOverlayBitmapRegion(0, 0, _previewWidth - 1, _previewHeight - 1);
                RebuildMaskRows(selectIndex: _selectedIndex);
            }

            Error = "";
            SyncNumericFieldsFromSelectedMask();
            UpdateFeatureExclusionStates();
            Status = $"마스크 #{_selectedIndex + 1} 수치 위치·크기 반영됨";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            Error = ex.Message;
        }
    }

    public bool CommitMasksToSession()
    {
        if (_roiId is not { } id || !_session.State.Rois.ContainsKey(id))
        {
            Error = "적용할 대상 ROI를 찾을 수 없습니다.";
            return false;
        }

        try
        {
            if (_draftPolygonPoints.Count >= 3)
            {
                TryCompleteDraftPolygon();
            }
            else if (_draftPolygonPoints.Count > 0)
            {
                _draftPolygonPoints.Clear();
                Notify(nameof(HasDraftPolygon));
            }

            SynchronizeCompiledBrushMasks();
            var immutableMasks = _draftMasks.ToImmutableArray();
            _session.ReplaceAllMasks(id, immutableMasks);
            Error = "";
            var msg = $"{_roiName}에 패턴 마스크 {immutableMasks.Length}개를 적용했습니다. (유효 특징점 {ActiveFeatureCount}개 / 제외 {ExcludedFeatureCount}개)";
            Status = msg;
            _statusCallback?.Invoke(msg);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            Error = ex.Message;
            return false;
        }
    }

    private void InitializeBrushMaskLayer(int width, int height)
    {
        int w = Math.Max(8, width);
        int h = Math.Max(8, height);
        _previewWidth = w;
        _previewHeight = h;
        _brushMaskGrid = new byte[checked(w * h)];
        _brushOverlayBgra = new byte[checked(w * h * 4)];
        _brushMaskBitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        _maskedBrushPixelCount = 0;
        _brushStrokeSegmentCount = 0;
        _singleStampEllipse = null;
        _draftPolygonPoints.Clear();
        Notify(nameof(BrushMaskImage));
        Notify(nameof(HasDraftPolygon));
    }

    private void EnsureBrushMaskLayerReady()
    {
        if (_brushMaskGrid is not null && _previewWidth > 0 && _previewHeight > 0)
        {
            return;
        }

        int w = _previewWidth > 0 ? _previewWidth : Math.Clamp((int)Math.Round(RoiWidth), 8, MaxPreviewDimension);
        int h = _previewHeight > 0 ? _previewHeight : Math.Clamp((int)Math.Round(RoiHeight), 8, MaxPreviewDimension);
        InitializeBrushMaskLayer(w, h);
    }

    private bool HasAnyBrushPixels() => _maskedBrushPixelCount > 0;

    private bool DoesCapsuleTouchVectorMask(
        MaskRegion mask,
        double x0Local,
        double y0Local,
        double x1Local,
        double y1Local,
        double radiusLocal)
    {
        if (_previewWidth <= 0 || _previewHeight <= 0)
        {
            return false;
        }

        double scaleX = _previewWidth / Math.Max(1.0, RoiWidth);
        double scaleY = _previewHeight / Math.Max(1.0, RoiHeight);
        double scaleAvg = 0.5 * (scaleX + scaleY);

        double gx0 = x0Local * scaleX;
        double gy0 = y0Local * scaleY;
        double gx1 = x1Local * scaleX;
        double gy1 = y1Local * scaleY;
        double radiusGrid = Math.Max(0.75, radiusLocal * scaleAvg);
        double radiusSq = radiusGrid * radiusGrid;

        int minX = Math.Clamp((int)Math.Floor(Math.Min(gx0, gx1) - radiusGrid - 1.0), 0, _previewWidth - 1);
        int maxX = Math.Clamp((int)Math.Ceiling(Math.Max(gx0, gx1) + radiusGrid + 1.0), 0, _previewWidth - 1);
        int minY = Math.Clamp((int)Math.Floor(Math.Min(gy0, gy1) - radiusGrid - 1.0), 0, _previewHeight - 1);
        int maxY = Math.Clamp((int)Math.Ceiling(Math.Max(gy0, gy1) + radiusGrid + 1.0), 0, _previewHeight - 1);

        double vx = gx1 - gx0;
        double vy = gy1 - gy0;
        double lenSq = (vx * vx) + (vy * vy);
        double invW = 1.0 / _previewWidth;
        double invH = 1.0 / _previewHeight;

        for (int y = minY; y <= maxY; y++)
        {
            double py = y + PixelCenterOffset;
            double normY = py * invH;
            for (int x = minX; x <= maxX; x++)
            {
                double px = x + PixelCenterOffset;
                double t = lenSq > 1e-8
                    ? Math.Clamp((((px - gx0) * vx) + ((py - gy0) * vy)) / lenSq, 0.0, 1.0)
                    : 0.0;

                double cx = gx0 + (t * vx);
                double cy = gy0 + (t * vy);
                double dx = px - cx;
                double dy = py - cy;

                if ((dx * dx) + (dy * dy) <= radiusSq &&
                    mask.ContainsNormalized(new PointD(px * invW, normY)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private void RasterizeVectorMaskIntoBrushGrid(
        MaskRegion mask,
        out int minX,
        out int minY,
        out int maxX,
        out int maxY)
    {
        minX = 0;
        minY = 0;
        maxX = 0;
        maxY = 0;
        if (_brushMaskGrid is null || _previewWidth <= 0 || _previewHeight <= 0)
        {
            return;
        }

        var b = mask.Bounds;
        minX = Math.Clamp((int)Math.Floor(b.Left * _previewWidth), 0, _previewWidth - 1);
        maxX = Math.Clamp((int)Math.Ceiling(b.Right * _previewWidth) - 1, 0, _previewWidth - 1);
        minY = Math.Clamp((int)Math.Floor(b.Top * _previewHeight), 0, _previewHeight - 1);
        maxY = Math.Clamp((int)Math.Ceiling(b.Bottom * _previewHeight) - 1, 0, _previewHeight - 1);

        double invW = 1.0 / _previewWidth;
        double invH = 1.0 / _previewHeight;

        for (int y = minY; y <= maxY; y++)
        {
            int rowOffset = y * _previewWidth;
            double normY = (y + PixelCenterOffset) * invH;
            for (int x = minX; x <= maxX; x++)
            {
                int idx = rowOffset + x;
                if (_shapeInsideMask is not null && _shapeInsideMask[idx] == 0)
                {
                    continue;
                }

                if (mask.ContainsNormalized(new PointD((x + PixelCenterOffset) * invW, normY)))
                {
                    SetBrushPixelValue(idx, byte.MaxValue);
                }
            }
        }
    }

    private bool RasterizePolygonOnBrushGrid(
        IReadOnlyList<PointD> localVertices,
        byte paintValue,
        out int dirtyMinX,
        out int dirtyMinY,
        out int dirtyMaxX,
        out int dirtyMaxY)
    {
        dirtyMinX = 0;
        dirtyMinY = 0;
        dirtyMaxX = 0;
        dirtyMaxY = 0;
        if (_brushMaskGrid is null || _previewWidth <= 0 || _previewHeight <= 0 || localVertices.Count < 3)
        {
            return false;
        }

        double scaleX = _previewWidth / Math.Max(1.0, RoiWidth);
        double scaleY = _previewHeight / Math.Max(1.0, RoiHeight);

        int count = localVertices.Count;
        Span<double> polyX = count <= 128 ? stackalloc double[count] : new double[count];
        Span<double> polyY = count <= 128 ? stackalloc double[count] : new double[count];

        double minGx = double.PositiveInfinity;
        double maxGx = double.NegativeInfinity;
        double minGy = double.PositiveInfinity;
        double maxGy = double.NegativeInfinity;

        for (int i = 0; i < count; i++)
        {
            double gx = localVertices[i].X * scaleX;
            double gy = localVertices[i].Y * scaleY;
            polyX[i] = gx;
            polyY[i] = gy;
            if (gx < minGx) minGx = gx;
            if (gx > maxGx) maxGx = gx;
            if (gy < minGy) minGy = gy;
            if (gy > maxGy) maxGy = gy;
        }

        if ((maxGx - minGx) < 0.5 || (maxGy - minGy) < 0.5)
        {
            return false;
        }

        int minX = Math.Clamp((int)Math.Floor(minGx - 1.0), 0, _previewWidth - 1);
        int maxX = Math.Clamp((int)Math.Ceiling(maxGx + 1.0), 0, _previewWidth - 1);
        int minY = Math.Clamp((int)Math.Floor(minGy - 1.0), 0, _previewHeight - 1);
        int maxY = Math.Clamp((int)Math.Ceiling(maxGy + 1.0), 0, _previewHeight - 1);

        dirtyMinX = minX;
        dirtyMinY = minY;
        dirtyMaxX = maxX;
        dirtyMaxY = maxY;

        Span<double> intersections = count <= 128 ? stackalloc double[count] : new double[count];
        bool modifiedAny = false;

        // 1. Scanline even-odd polygon interior fill
        for (int y = minY; y <= maxY; y++)
        {
            double scanY = y + PixelCenterOffset;
            int nodeCount = 0;
            int j = count - 1;

            for (int i = 0; i < count; i++)
            {
                double yi = polyY[i];
                double yj = polyY[j];
                if ((yi < scanY && yj >= scanY) || (yj < scanY && yi >= scanY))
                {
                    double xi = polyX[i];
                    double xj = polyX[j];
                    intersections[nodeCount++] = xi + (((scanY - yi) / (yj - yi)) * (xj - xi));
                }

                j = i;
            }

            intersections.Slice(0, nodeCount).Sort();

            int rowOffset = y * _previewWidth;
            for (int k = 0; k + 1 < nodeCount; k += 2)
            {
                int xStart = Math.Clamp((int)Math.Ceiling(intersections[k] - PixelCenterOffset), minX, maxX);
                int xEnd = Math.Clamp((int)Math.Floor(intersections[k + 1] - PixelCenterOffset), minX, maxX);
                for (int x = xStart; x <= xEnd; x++)
                {
                    int idx = rowOffset + x;
                    if (paintValue != 0 && _shapeInsideMask is not null && _shapeInsideMask[idx] == 0)
                    {
                        continue;
                    }

                    SetBrushPixelValue(idx, paintValue);
                    modifiedAny = true;
                }
            }
        }

        // 2. Rasterize polygon boundary edges (0.85 px radius) so acute corners and boundary pixels are crisp
        double edgeRadiusLocal = 0.85 / Math.Max(0.1, 0.5 * (scaleX + scaleY));
        for (int i = 0; i < count; i++)
        {
            var a = localVertices[i];
            var b = localVertices[(i + 1) % count];
            if (RasterizeCapsuleOnBrushGrid(a.X, a.Y, b.X, b.Y, edgeRadiusLocal, paintValue, out _, out _, out _, out _))
            {
                modifiedAny = true;
            }
        }

        return modifiedAny;
    }

    private bool RasterizeCapsuleOnBrushGrid(
        double x0Local,
        double y0Local,
        double x1Local,
        double y1Local,
        double radiusLocal,
        byte paintValue,
        out int dirtyMinX,
        out int dirtyMinY,
        out int dirtyMaxX,
        out int dirtyMaxY)
    {
        dirtyMinX = 0;
        dirtyMinY = 0;
        dirtyMaxX = 0;
        dirtyMaxY = 0;
        if (_brushMaskGrid is null || _previewWidth <= 0 || _previewHeight <= 0)
        {
            return false;
        }

        double scaleX = _previewWidth / Math.Max(1.0, RoiWidth);
        double scaleY = _previewHeight / Math.Max(1.0, RoiHeight);
        double scaleAvg = 0.5 * (scaleX + scaleY);

        double gx0 = x0Local * scaleX;
        double gy0 = y0Local * scaleY;
        double gx1 = x1Local * scaleX;
        double gy1 = y1Local * scaleY;
        double radiusGrid = Math.Max(0.75, radiusLocal * scaleAvg);
        double radiusSq = radiusGrid * radiusGrid;

        int minX = Math.Clamp((int)Math.Floor(Math.Min(gx0, gx1) - radiusGrid - 1.0), 0, _previewWidth - 1);
        int maxX = Math.Clamp((int)Math.Ceiling(Math.Max(gx0, gx1) + radiusGrid + 1.0), 0, _previewWidth - 1);
        int minY = Math.Clamp((int)Math.Floor(Math.Min(gy0, gy1) - radiusGrid - 1.0), 0, _previewHeight - 1);
        int maxY = Math.Clamp((int)Math.Ceiling(Math.Max(gy0, gy1) + radiusGrid + 1.0), 0, _previewHeight - 1);

        dirtyMinX = minX;
        dirtyMinY = minY;
        dirtyMaxX = maxX;
        dirtyMaxY = maxY;

        double vx = gx1 - gx0;
        double vy = gy1 - gy0;
        double lenSq = (vx * vx) + (vy * vy);
        bool modifiedAny = false;

        for (int y = minY; y <= maxY; y++)
        {
            double py = y + PixelCenterOffset;
            int rowOffset = y * _previewWidth;
            for (int x = minX; x <= maxX; x++)
            {
                int idx = rowOffset + x;
                if (paintValue != 0 && _shapeInsideMask is not null && _shapeInsideMask[idx] == 0)
                {
                    continue;
                }

                double px = x + PixelCenterOffset;
                double t = lenSq > 1e-8
                    ? Math.Clamp((((px - gx0) * vx) + ((py - gy0) * vy)) / lenSq, 0.0, 1.0)
                    : 0.0;

                double cx = gx0 + (t * vx);
                double cy = gy0 + (t * vy);
                double dx = px - cx;
                double dy = py - cy;

                if ((dx * dx) + (dy * dy) <= radiusSq)
                {
                    if (SetBrushPixelValue(idx, paintValue) || paintValue != 0)
                    {
                        modifiedAny = true;
                    }
                }
            }
        }

        return modifiedAny;
    }

    private bool SetBrushPixelValue(int idx, byte newValue)
    {
        byte oldValue = _brushMaskGrid![idx];
        if (oldValue == newValue)
        {
            return false;
        }

        _brushMaskGrid[idx] = newValue;
        if (newValue != 0)
        {
            _maskedBrushPixelCount++;
        }
        else
        {
            _maskedBrushPixelCount = Math.Max(0, _maskedBrushPixelCount - 1);
        }

        return true;
    }

    private void FillNormalizedBoundsOnBrushGrid(BoundsD normBounds, byte value)
    {
        if (_brushMaskGrid is null || _previewWidth <= 0 || _previewHeight <= 0)
        {
            return;
        }

        int minX = Math.Clamp((int)Math.Floor(normBounds.Left * _previewWidth), 0, _previewWidth - 1);
        int maxX = Math.Clamp((int)Math.Ceiling(normBounds.Right * _previewWidth) - 1, 0, _previewWidth - 1);
        int minY = Math.Clamp((int)Math.Floor(normBounds.Top * _previewHeight), 0, _previewHeight - 1);
        int maxY = Math.Clamp((int)Math.Ceiling(normBounds.Bottom * _previewHeight) - 1, 0, _previewHeight - 1);

        for (int y = minY; y <= maxY; y++)
        {
            int rowOffset = y * _previewWidth;
            for (int x = minX; x <= maxX; x++)
            {
                SetBrushPixelValue(rowOffset + x, value);
            }
        }
    }

    private void UpdateBrushOverlayBitmapRegion(int minX, int minY, int maxX, int maxY)
    {
        if (_brushMaskGrid is null || _brushOverlayBgra is null || _brushMaskBitmap is null || _previewWidth <= 0 || _previewHeight <= 0)
        {
            return;
        }

        int w = _previewWidth;
        int h = _previewHeight;
        int x0 = Math.Clamp(minX - 1, 0, w - 1);
        int x1 = Math.Clamp(maxX + 1, 0, w - 1);
        int y0 = Math.Clamp(minY - 1, 0, h - 1);
        int y1 = Math.Clamp(maxY + 1, 0, h - 1);

        for (int y = y0; y <= y1; y++)
        {
            int rowOffset = y * w;
            for (int x = x0; x <= x1; x++)
            {
                int idx = rowOffset + x;
                int bgraOffset = idx * 4;
                if (_brushMaskGrid[idx] == 0)
                {
                    _brushOverlayBgra[bgraOffset + 0] = 0;
                    _brushOverlayBgra[bgraOffset + 1] = 0;
                    _brushOverlayBgra[bgraOffset + 2] = 0;
                    _brushOverlayBgra[bgraOffset + 3] = 0;
                    continue;
                }

                bool isBoundary =
                    x == 0 || x == w - 1 || y == 0 || y == h - 1 ||
                    _brushMaskGrid[idx - 1] == 0 ||
                    _brushMaskGrid[idx + 1] == 0 ||
                    _brushMaskGrid[idx - w] == 0 ||
                    _brushMaskGrid[idx + w] == 0;

                if (isBoundary)
                {
                    // Crisp rose-red contour outline (#F43F5E)
                    _brushOverlayBgra[bgraOffset + 0] = 94;
                    _brushOverlayBgra[bgraOffset + 1] = 63;
                    _brushOverlayBgra[bgraOffset + 2] = 244;
                    _brushOverlayBgra[bgraOffset + 3] = 235;
                }
                else
                {
                    // Uniform semi-transparent rose-red interior fill (#E03360, alpha=102)
                    _brushOverlayBgra[bgraOffset + 0] = 96;
                    _brushOverlayBgra[bgraOffset + 1] = 51;
                    _brushOverlayBgra[bgraOffset + 2] = 224;
                    _brushOverlayBgra[bgraOffset + 3] = 102;
                }
            }
        }

        int dirtyWidth = (x1 - x0) + 1;
        int dirtyRows = (y1 - y0) + 1;
        _brushMaskBitmap.WritePixels(
            new Int32Rect(x0, y0, dirtyWidth, dirtyRows),
            _brushOverlayBgra,
            w * 4,
            ((y0 * w) + x0) * 4);
    }

    /// <summary>
    /// 2D 브러시 래스터 그리드를 수직 병합된 최소 개수의 RLE 직사각형 MaskRegion 집합으로 변환한다.
    /// 브러시 획이 아무리 복잡해도 적응형 행/열 병합을 통해 MaximumMasksPerRoi 예산을 절대 초과하지 않는다.
    /// </summary>
    private void SynchronizeCompiledBrushMasks()
    {
        _brushCompiledMasks.Clear();

        if (_brushMaskGrid is not null && _previewWidth > 0 && _previewHeight > 0)
        {
            if (_singleStampEllipse is not null && _brushStrokeSegmentCount == 1)
            {
                _brushCompiledMasks.Add(_singleStampEllipse);
            }
            else if (HasAnyBrushPixels())
            {
                int availableBudget = Math.Max(1, EditorSession.MaximumMasksPerRoi - _vectorMasks.Count);
                int rowStep = 1;
                int colBridgeTolerance = 0;

                while (true)
                {
                    CompileRleRectanglesFromBrushGrid(rowStep, colBridgeTolerance, _brushCompiledMasks);
                    if (_brushCompiledMasks.Count <= availableBudget || rowStep >= 32)
                    {
                        break;
                    }

                    rowStep *= 2;
                    colBridgeTolerance = Math.Min(16, (colBridgeTolerance * 2) + 1);
                }

                if (_brushCompiledMasks.Count > availableBudget)
                {
                    _brushCompiledMasks.RemoveRange(availableBudget, _brushCompiledMasks.Count - availableBudget);
                }
            }
        }

        _draftMasks.Clear();
        _draftMasks.AddRange(_vectorMasks);
        _draftMasks.AddRange(_brushCompiledMasks);
    }

    private void CompileRleRectanglesFromBrushGrid(int rowStep, int colTolerance, List<MaskRegion> output)
    {
        output.Clear();
        if (_brushMaskGrid is null)
        {
            return;
        }

        int w = _previewWidth;
        int h = _previewHeight;
        double invW = 1.0 / w;
        double invH = 1.0 / h;

        List<(int StartCol, int EndCol, int TopRow, int BottomRow)> activeRects = [];
        List<(int StartCol, int EndCol)> rowRuns = [];

        for (int y = 0; y < h; y += rowStep)
        {
            int bandEndY = Math.Min(h - 1, (y + rowStep) - 1);
            rowRuns.Clear();

            int c = 0;
            while (c < w)
            {
                while (c < w && !IsBandColumnMasked(c, y, bandEndY, w))
                {
                    c++;
                }

                if (c >= w)
                {
                    break;
                }

                int runStart = c;
                c++;
                while (c < w)
                {
                    if (IsBandColumnMasked(c, y, bandEndY, w))
                    {
                        c++;
                    }
                    else if (colTolerance > 0 && HasMaskedWithinColTolerance(c, colTolerance, y, bandEndY, w))
                    {
                        c++;
                    }
                    else
                    {
                        break;
                    }
                }

                rowRuns.Add((runStart, c - 1));
            }

            // Match current band runs to active rectangles from the previous band
            for (int r = 0; r < rowRuns.Count; r++)
            {
                var (startCol, endCol) = rowRuns[r];
                int matchedIdx = -1;

                for (int a = 0; a < activeRects.Count; a++)
                {
                    var active = activeRects[a];
                    if (active.BottomRow == y - 1 &&
                        Math.Abs(active.StartCol - startCol) <= colTolerance &&
                        Math.Abs(active.EndCol - endCol) <= colTolerance)
                    {
                        matchedIdx = a;
                        break;
                    }
                }

                if (matchedIdx >= 0)
                {
                    var active = activeRects[matchedIdx];
                    activeRects[matchedIdx] = (
                        Math.Min(active.StartCol, startCol),
                        Math.Max(active.EndCol, endCol),
                        active.TopRow,
                        bandEndY);
                }
                else
                {
                    activeRects.Add((startCol, endCol, y, bandEndY));
                }
            }

            // Flush any active rectangles that did not continue into this band
            for (int a = activeRects.Count - 1; a >= 0; a--)
            {
                var active = activeRects[a];
                if (active.BottomRow < bandEndY)
                {
                    EmitMaskRectangle(active.StartCol, active.TopRow, active.EndCol, active.BottomRow, invW, invH, output);
                    activeRects.RemoveAt(a);
                }
            }
        }

        for (int a = 0; a < activeRects.Count; a++)
        {
            var active = activeRects[a];
            EmitMaskRectangle(active.StartCol, active.TopRow, active.EndCol, active.BottomRow, invW, invH, output);
        }
    }

    private bool IsBandColumnMasked(int col, int yStart, int yEnd, int width)
    {
        for (int y = yStart; y <= yEnd; y++)
        {
            if (_brushMaskGrid![(y * width) + col] != 0)
            {
                return true;
            }
        }

        return false;
    }

    private bool HasMaskedWithinColTolerance(int col, int tolerance, int yStart, int yEnd, int width)
    {
        int maxLookahead = Math.Min(width - 1, col + tolerance);
        for (int nextC = col + 1; nextC <= maxLookahead; nextC++)
        {
            if (IsBandColumnMasked(nextC, yStart, yEnd, width))
            {
                return true;
            }
        }

        return false;
    }

    private static void EmitMaskRectangle(
        int startCol,
        int topRow,
        int endCol,
        int bottomRow,
        double invW,
        double invH,
        List<MaskRegion> output)
    {
        double left = Math.Clamp(startCol * invW, 0.0, 1.0);
        double top = Math.Clamp(topRow * invH, 0.0, 1.0);
        double right = Math.Clamp((endCol + 1) * invW, 0.0, 1.0);
        double bottom = Math.Clamp((bottomRow + 1) * invH, 0.0, 1.0);

        if (right - left <= 1e-6)
        {
            right = Math.Min(1.0, left + MinMaskNormalizedSpan);
        }

        if (bottom - top <= 1e-6)
        {
            bottom = Math.Min(1.0, top + MinMaskNormalizedSpan);
        }

        if (right > left && bottom > top)
        {
            output.Add(new MaskRegion(MaskShape.Rectangle, new BoundsD(left, top, right, bottom)));
        }
    }

    private void ClearTarget(string message)
    {
        _roiId = null;
        _roiName = "";
        _roiGeometry = null;
        _rectifiedPixels = null;
        _shapeInsideMask = null;
        _previewBitmap = null;
        _brushMaskGrid = null;
        _brushOverlayBgra = null;
        _brushMaskBitmap = null;
        _maskedBrushPixelCount = 0;
        _cachedDipoles = [];
        _vectorMasks.Clear();
        _brushCompiledMasks.Clear();
        _draftMasks.Clear();
        _draftPolygonPoints.Clear();
        Rows.Clear();
        _featurePoints = [];
        _activeFeatureCount = 0;
        _excludedFeatureCount = 0;
        _selectedIndex = -1;
        Status = message;
        Error = "";
        NotifyAllProperties();
        VisualStateChanged?.Invoke();
    }

    private void RebuildMaskRows(int selectIndex)
    {
        Rows.Clear();
        for (var i = 0; i < _draftMasks.Count; i++)
        {
            var m = _draftMasks[i];
            var isBrushRegion = i >= _vectorMasks.Count;
            var shapeLabel = isBrushRegion
                ? (m.Shape == MaskShape.Ellipse ? "브러시 스탬프" : "브러시 영역")
                : (m.Shape == MaskShape.Rectangle ? "사각형" : "타원");
            var pxW = m.Bounds.Width * RoiWidth;
            var pxH = m.Bounds.Height * RoiHeight;
            Rows.Add(new MaskRow(i, $"{i + 1:00} · {shapeLabel} ({pxW:0.#}×{pxH:0.#} px)"));
        }

        _selectedIndex = selectIndex >= 0 && selectIndex < _draftMasks.Count ? selectIndex : -1;
        SyncNumericFieldsFromSelectedMask();
        Notify(nameof(SelectedIndex));
        Notify(nameof(HasSelectedMask));
        Notify(nameof(MaskCount));
        RefreshCommands();
    }

    private void SyncNumericFieldsFromSelectedMask()
    {
        if (!HasSelectedMask)
        {
            X = "0";
            Y = "0";
            Width = "0";
            Height = "0";
            return;
        }

        var bounds = _draftMasks[_selectedIndex].Bounds;
        X = FormatNumber(bounds.Left * RoiWidth);
        Y = FormatNumber(bounds.Top * RoiHeight);
        Width = FormatNumber(bounds.Width * RoiWidth);
        Height = FormatNumber(bounds.Height * RoiHeight);
    }

    /// <summary>
    /// 영상 로드 또는 EdgeThreshold 변경 시에만 SubPixelEdgeExtractor를 수행하여 후보 에지 다이폴을 캐싱한다.
    /// </summary>
    private void ExtractRawEdgeDipoles()
    {
        if (_rectifiedPixels is null || _previewWidth < 4 || _previewHeight < 4 || _roiGeometry is null)
        {
            _cachedDipoles = [];
            _featurePoints = [];
            return;
        }

        var maxDipoleCapacity = Math.Clamp((_previewWidth * _previewHeight) / 2, 64, 16_384);
        using var dipoles = EdgeDipoleSoA.Rent(maxDipoleCapacity);
        // [Math Why]: SubPixelEdgeExtractor already adds +0.5f pixel-center offset internally: subX = (x + 0.5f + nx * offset) * scaleToBase + originOffsetX.
        // Passing originOffset = -(W * 0.5f) - 0.5f ensures px = xSpan[i] + (W * 0.5) + 0.5 maps pixel index x to its exact center (x + 0.5), matching ExtractRectifiedRoiPatchAsync.
        SubPixelEdgeExtractor.Extract(
            _rectifiedPixels,
            _previewWidth,
            _previewHeight,
            _previewWidth,
            (float)_edgeThreshold,
            dipoles,
            scaleToBase: 1.0f,
            originOffsetX: -(_previewWidth * 0.5f) - (float)PixelCenterOffset,
            originOffsetY: -(_previewHeight * 0.5f) - (float)PixelCenterOffset);

        if (dipoles.IsEmpty)
        {
            _cachedDipoles = [];
            _featurePoints = [];
            return;
        }

        var scaleX = RoiWidth / _previewWidth;
        var scaleY = RoiHeight / _previewHeight;
        var halfWPlusCenter = (_previewWidth * 0.5) + PixelCenterOffset;
        var halfHPlusCenter = (_previewHeight * 0.5) + PixelCenterOffset;
        var xSpan = dipoles.X;
        var ySpan = dipoles.Y;
        var cosSpan = dipoles.CosTheta;
        var sinSpan = dipoles.SinTheta;

        var stride = Math.Max(1, (dipoles.Count + MaxOverlayFeatureMarkers - 1) / MaxOverlayFeatureMarkers);
        var list = new List<CachedEdgeDipole>(dipoles.Count);
        var markerCount = 0;

        for (var i = 0; i < dipoles.Count; i++)
        {
            var px = xSpan[i] + halfWPlusCenter;
            var py = ySpan[i] + halfHPlusCenter;
            var ix = Math.Clamp((int)Math.Floor(px), 0, _previewWidth - 1);
            var iy = Math.Clamp((int)Math.Floor(py), 0, _previewHeight - 1);

            if (_shapeInsideMask is not null && _shapeInsideMask[(iy * _previewWidth) + ix] == 0)
            {
                continue;
            }

            var normX = Math.Clamp(px / _previewWidth, 0.0, 1.0);
            var normY = Math.Clamp(py / _previewHeight, 0.0, 1.0);
            var isMarker = (i % stride) == 0;
            if (isMarker)
            {
                markerCount++;
            }

            list.Add(new CachedEdgeDipole(
                LocalX: px * scaleX,
                LocalY: py * scaleY,
                CosTheta: cosSpan[i],
                SinTheta: sinSpan[i],
                NormalizedPoint: new PointD(normX, normY),
                PreviewX: ix,
                PreviewY: iy,
                IsSubsampledMarker: isMarker));
        }

        _cachedDipoles = list.ToArray();
        _featurePoints = markerCount > 0 ? new PatternMaskFeaturePoint[markerCount] : [];
    }

    /// <summary>
    /// 캐싱된 에지 다이폴에 대해 O(1) 브러시 그리드 조회 및 벡터 마스크 포함 여부만 고속 평가한다 (~0.02 ms).
    /// </summary>
    private void UpdateFeatureExclusionStates()
    {
        if (_cachedDipoles.Length == 0)
        {
            _featurePoints = [];
            _activeFeatureCount = 0;
            _excludedFeatureCount = 0;
            Notify(nameof(FeaturePoints));
            Notify(nameof(ActiveFeatureCount));
            Notify(nameof(ExcludedFeatureCount));
            Notify(nameof(FeatureSummary));
            VisualStateChanged?.Invoke();
            return;
        }

        var activeCount = 0;
        var excludedCount = 0;
        var markerIdx = 0;
        var hasBrushGrid = _brushMaskGrid is not null && _previewWidth > 0 && _previewHeight > 0;
        var vectorCount = _vectorMasks.Count;

        for (var i = 0; i < _cachedDipoles.Length; i++)
        {
            ref readonly var d = ref _cachedDipoles[i];
            var excluded = hasBrushGrid && _brushMaskGrid![(d.PreviewY * _previewWidth) + d.PreviewX] != 0;

            if (!excluded && vectorCount > 0)
            {
                for (var m = 0; m < vectorCount; m++)
                {
                    if (_vectorMasks[m].ContainsNormalized(d.NormalizedPoint))
                    {
                        excluded = true;
                        break;
                    }
                }
            }

            if (excluded)
            {
                excludedCount++;
            }
            else
            {
                activeCount++;
            }

            if (d.IsSubsampledMarker && markerIdx < _featurePoints.Length)
            {
                var existing = _featurePoints[markerIdx];
                if (existing is null || existing.IsExcluded != excluded)
                {
                    _featurePoints[markerIdx] = new PatternMaskFeaturePoint(
                        d.LocalX,
                        d.LocalY,
                        d.CosTheta,
                        d.SinTheta,
                        excluded);
                }

                markerIdx++;
            }
        }

        _activeFeatureCount = activeCount;
        _excludedFeatureCount = excludedCount;

        if (!_isInteractiveStrokeActive)
        {
            Notify(nameof(ActiveFeatureCount));
            Notify(nameof(ExcludedFeatureCount));
            Notify(nameof(FeatureSummary));
        }

        VisualStateChanged?.Invoke();
    }

    private void RebuildPreviewBitmap()
    {
        if (_rectifiedPixels is null || _previewWidth <= 0 || _previewHeight <= 0)
        {
            _previewBitmap = null;
            Notify(nameof(PreviewImage));
            return;
        }

        var bgra = new byte[_previewWidth * _previewHeight * 4];
        for (var i = 0; i < _rectifiedPixels.Length; i++)
        {
            var v = _rectifiedPixels[i];
            var outsideShape = _shapeInsideMask is not null && _shapeInsideMask[i] == 0;
            var offset = i * 4;
            if (outsideShape)
            {
                bgra[offset + 0] = (byte)(v / 3);
                bgra[offset + 1] = (byte)(v / 3);
                bgra[offset + 2] = (byte)(v / 3);
                bgra[offset + 3] = 255;
            }
            else
            {
                bgra[offset + 0] = v;
                bgra[offset + 1] = v;
                bgra[offset + 2] = v;
                bgra[offset + 3] = 255;
            }
        }

        var bmp = new WriteableBitmap(_previewWidth, _previewHeight, 96, 96, PixelFormats.Bgra32, null);
        bmp.WritePixels(new Int32Rect(0, 0, _previewWidth, _previewHeight), bgra, _previewWidth * 4, 0);
        bmp.Freeze();
        _previewBitmap = bmp;
        Notify(nameof(PreviewImage));
    }

    private async Task<(byte[] Pixels, byte[]? ShapeMask, int Width, int Height)> ExtractRectifiedRoiPatchAsync(
        ImageReference image,
        bool live,
        RoiGeometry roi,
        CancellationToken token)
    {
        using var reader = await MatchingImageReader.OpenAsync(image, live, _loader, _retainFrame, token, _preFilterPipeline).ConfigureAwait(false);
        reader.PreferDirectGray = true;

        var bounds = roi.Bounds;
        var pad = 4;
        var left = Math.Max(0, (int)Math.Floor(bounds.Left) - pad);
        var top = Math.Max(0, (int)Math.Floor(bounds.Top) - pad);
        var right = Math.Min(image.Width, (int)Math.Ceiling(bounds.Right) + pad);
        var bottom = Math.Min(image.Height, (int)Math.Ceiling(bounds.Bottom) + pad);
        var cropW = Math.Max(1, right - left);
        var cropH = Math.Max(1, bottom - top);

        var crop = ReadChunkedGrayImage(reader, left, top, cropW, cropH, token);
        var maxDim = Math.Max(roi.Width, roi.Height);
        var scale = Math.Min(1.0, MaxPreviewDimension / Math.Max(1.0, maxDim));
        var dstW = Math.Clamp((int)Math.Round(roi.Width * scale), 8, MaxPreviewDimension);
        var dstH = Math.Clamp((int)Math.Round(roi.Height * scale), 8, MaxPreviewDimension);

        var rectified = new byte[checked(dstW * dstH)];
        var shapeMask = BuildLocalShapeInsideMask(roi, dstW, dstH);

        var cx = roi.Center.X - left;
        var cy = roi.Center.Y - top;
        var rad = roi.Angle * (Math.PI / 180.0);
        var cos = Math.Cos(rad);
        var sin = Math.Sin(rad);
        var stepX = roi.Width / dstW;
        var stepY = roi.Height / dstH;
        var startLocalX = (-0.5 * roi.Width) + (0.5 * stepX);
        var startLocalY = (-0.5 * roi.Height) + (0.5 * stepY);
        var srcSpan = crop.Pixels.Span;
        var srcStride = crop.Stride;

        for (var y = 0; y < dstH; y++)
        {
            token.ThrowIfCancellationRequested();
            var ly = startLocalY + (y * stepY);
            var rowOffset = y * dstW;
            for (var x = 0; x < dstW; x++)
            {
                var lx = startLocalX + (x * stepX);
                var sx = cx + (lx * cos) - (ly * sin) - 0.5;
                var sy = cy + (lx * sin) + (ly * cos) - 0.5;
                var ix = Math.Clamp((int)Math.Floor(sx), 0, cropW - 1);
                var iy = Math.Clamp((int)Math.Floor(sy), 0, cropH - 1);
                var ix1 = Math.Min(cropW - 1, ix + 1);
                var iy1 = Math.Min(cropH - 1, iy + 1);
                var fx = Math.Clamp(sx - ix, 0.0, 1.0);
                var fy = Math.Clamp(sy - iy, 0.0, 1.0);

                var p00 = srcSpan[(iy * srcStride) + ix];
                var p10 = srcSpan[(iy * srcStride) + ix1];
                var p01 = srcSpan[(iy1 * srcStride) + ix];
                var p11 = srcSpan[(iy1 * srcStride) + ix1];

                var topVal = p00 + ((p10 - p00) * fx);
                var botVal = p01 + ((p11 - p01) * fx);
                var val = (int)Math.Round(topVal + ((botVal - topVal) * fy));
                rectified[rowOffset + x] = (byte)Math.Clamp(val, byte.MinValue, byte.MaxValue);
            }
        }

        return (rectified, shapeMask, dstW, dstH);
    }

    private static VisionAlgorithm.ShapeGrayImage ReadChunkedGrayImage(
        MatchingImageReader reader,
        int left,
        int top,
        int width,
        int height,
        CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(width, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(height, 0);
        if ((long)width * height > MaxReaderPixelsBudget)
        {
            throw new ArgumentException($"패턴 ROI 영역이 {MaxReaderPixelsBudget:N0} pixel 작업 예산을 초과했습니다.", nameof(width));
        }

        if ((long)width * height <= ReaderWindowBudgetPixels)
        {
            return reader.Read(left, top, width, height, token, ReaderWindowBudgetPixels);
        }

        var pixels = new byte[checked(width * height)];
        var chunkHeight = Math.Max(1, ReaderWindowBudgetPixels / width);
        for (var y = 0; y < height; y += chunkHeight)
        {
            token.ThrowIfCancellationRequested();
            var rows = Math.Min(chunkHeight, height - y);
            var chunk = reader.Read(left, top + y, width, rows, token, ReaderWindowBudgetPixels);
            for (var r = 0; r < rows; r++)
            {
                chunk.Pixels.Span.Slice(r * chunk.Stride, width).CopyTo(pixels.AsSpan((y + r) * width, width));
            }
        }

        return new VisionAlgorithm.ShapeGrayImage(pixels, width, height, width);
    }

    private static byte[]? BuildLocalShapeInsideMask(RoiGeometry roi, int dstW, int dstH)
    {
        if (roi.Shape.Kind == RoiShapeKind.Rectangle)
        {
            return null;
        }

        var mask = new byte[checked(dstW * dstH)];
        var stepX = roi.Width / dstW;
        var stepY = roi.Height / dstH;
        var startLocalX = (-0.5 * roi.Width) + (0.5 * stepX);
        var startLocalY = (-0.5 * roi.Height) + (0.5 * stepY);

        for (var y = 0; y < dstH; y++)
        {
            var ly = startLocalY + (y * stepY);
            var rowOffset = y * dstW;
            for (var x = 0; x < dstW; x++)
            {
                var lx = startLocalX + (x * stepX);
                if (roi.Shape.ContainsLocal(new PointD(lx, ly), roi.Width, roi.Height, 0))
                {
                    mask[rowOffset + x] = byte.MaxValue;
                }
            }
        }

        return mask;
    }

    private void RefreshCommands()
    {
        RemoveSelectedCommand.Refresh();
        ClearAllCommand.Refresh();
        ResetToSavedCommand.Refresh();
        ApplyNumericBoundsCommand.Refresh();
        CommitToRoiCommand.Refresh();
    }

    private void NotifyAllProperties()
    {
        Notify(null);
        RefreshCommands();
    }

    private static string FormatNumber(double value) => value.ToString("0.###", CultureInfo.CurrentCulture);

    private static double ParseNormalized(string text, double original, double length)
    {
        if (text == FormatNumber(original * length))
        {
            return original;
        }

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) || !double.IsFinite(value))
        {
            throw new ArgumentException("위치와 크기에 유효한 숫자를 입력하세요.");
        }

        return value / Math.Max(1e-6, length);
    }
}
