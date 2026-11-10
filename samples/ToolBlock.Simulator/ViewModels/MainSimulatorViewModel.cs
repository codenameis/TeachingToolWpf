// File: samples/ToolBlock.Simulator/ViewModels/MainSimulatorViewModel.cs
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Win32;
using ToolBlock.Simulator.Commands;
using ToolBlock.Simulator.Models;
using ToolBlock.Simulator.Services;
using VisionAlgorithm.OpenSrc.Common;
using VisionAlgorithm.OpenSrc.ImageJ;
using VisionAlgorithm.OpenSrc.ToolBlock;
using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Geometry;
using VisionCanvas.Core.Interaction;
using VisionCanvas.Imaging.Filters;
using VisionCanvas.Presentation;
using VisionCanvas.Rendering;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace ToolBlock.Simulator.ViewModels;

/// <summary>
/// Primary ViewModel for the ImageJ + ToolBlock Vision Workbench.
/// Organizes all tools into 3 clean, cohesive categories requested by the user:
/// <list type="number">
/// <item><description><b>1. 필터 툴 (Filter Tools)</b>: Brightness/Contrast, 6-Method Auto-Threshold, and 17 Spatial / Morphology / Background Filters with zero-full-allocation ROI live preview.</description></item>
/// <item><description><b>2. 확인 툴 (Check / Verification Tools)</b>: 64-Bin Histogram, 1D Plot Profile + Edge Gradient, On-Demand Blob / Particle Check, and ROI Quantitative Measure (<c>Ctrl+M</c>).</description></item>
/// <item><description><b>3. 검사 툴 (Inspection Tools)</b>: Proprietary ToolBlock DAG Inspection (<c>ShapePatternMatch</c>, <c>DualRakeCornerAlign</c>, <c>SubpixelCaliperMetrology</c>, <c>RleBlobAnalyzer</c>, <c>BinaryPixelCounter</c>, <c>GoldenSurfaceInspector</c>, <c>GeometryMathCombiner</c>, <c>ToleranceSpecJudge</c>) with OK/NG verdict and SPC/Replay output.</description></item>
/// </list>
/// Streams huge disk images (such as <c>ken32.png</c> at <c>17,280 × 30,720</c> = 530 MP) directly via <see cref="ImageCoordinator"/>
/// without full-image byte[] allocation, and starts with an empty workspace (no default image loaded on startup).
/// </summary>
public sealed class MainSimulatorViewModel : INotifyPropertyChanged, IDisposable
{
    private const int DisplayHistogramBarCount = 64;
    private const int BinsPerDisplayBar = 4;
    private const int DisplayProfileSampleCount = 64;
    private const int MaxOverlayParticleMarkers = 256;
    private const int MaxFilterPreviewBudgetPixels = 1_048_576;
    private const int MaxInspectionWindowPixels = 2_097_152;
    private const double MaxHistogramChartHeightPx = 112.0;
    private const double MinVisibleBarHeightPx = 2.0;
    private const double ZoomInStepFactor = 1.25;
    private const double ZoomOutStepFactor = 0.80;
    private const byte DefaultFillValue = byte.MaxValue;
    private const byte DefaultThresholdLow = 110;
    private const byte DefaultThresholdHigh = byte.MaxValue;
    private const int DefaultMinParticleArea = 12;
    private const int DefaultMaxParticleArea = 500_000;
    private const float DefaultMinCircularity = 0.0f;
    private const float DefaultMaxCircularity = 1.0f;

    private static readonly Vector4 OverlayPassColor = new(0.06f, 0.73f, 0.51f, 1.0f);
    private static readonly Vector4 OverlayRoiColor = new(0.22f, 0.74f, 0.97f, 1.0f);

    private readonly EditorSession _editor;
    private readonly InteractionController _rawInput;
    private readonly ImageCoordinator _images;
    private readonly ToolBlockDynamicImageLoader _loader;
    private readonly ImageJWorkbenchEngine _engine = new();
    private readonly DynamicToolBlockRuntime _toolBlockRuntime = new();

    private readonly List<EditableToolNodeSpec> _inspectionSpecs = [];
    private readonly List<EditablePortLink> _inspectionLinks = [];

    private int _activeWidth;
    private int _activeHeight;
    private int _activeStride;
    private long _liveFrameCounter = 1;
    private int _measurementSequence = 1;
    private int _nextInspectionNodeId = 1;
    private bool _wasPreviewingRoiTransform;
    private bool _suppressEvents;
    private bool _disposed;

    private LoadedImageFrameItem? _selectedStackFrame;
    private CanvasRoiOptionItem? _selectedRoiOption;
    private ImageJFilterOptionItem _selectedFilterOption;
    private ToolboxPaletteItem _selectedPaletteTool;
    private DagNodeDisplayItem? _selectedInspectionNode;
    private FilterPreviewResult? _activeFilterPreview;
    private ResultOverlay? _particlesOverlay;
    private ResultOverlay? _inspectionOverlay;

    // Top-level 3 Category Mode Index (0 = 필터 툴, 1 = 확인 툴, 2 = 검사 툴)
    private int _selectedToolCategoryIndex;
    private int _selectedBottomTabIndex;

    // ==================== Category 1: 필터 툴 (Filter Tools) ====================
    private int _contrastDisplayMin;
    private int _contrastDisplayMax = byte.MaxValue;
    private float _contrastGamma = ImageJContrastSpec.NeutralGamma;
    private bool _isLiveContrastPreviewEnabled;

    private ImageJAutoThresholdMethod _selectedAutoThresholdMethod = ImageJAutoThresholdMethod.Otsu;
    private int _thresholdMin = DefaultThresholdLow;
    private int _thresholdMax = DefaultThresholdHigh;
    private bool _isDarkBackground = true;
    private bool _isLiveThresholdPreviewEnabled;

    private byte _lastOtsuThreshold = 128;
    private byte _lastTriangleThreshold = 128;
    private byte _lastIsoDataThreshold = 128;
    private byte _lastMaxEntropyThreshold = 128;
    private byte _lastMeanThreshold = 128;
    private byte _lastMedianThreshold = 128;

    private bool _isLiveFilterPreviewEnabled;
    private int _fillGrayValue = DefaultFillValue;

    // ==================== Category 2: 확인 툴 (Check / Verify Tools) ====================
    private bool _isVerticalProfileMode;
    private int _particleMinArea = DefaultMinParticleArea;
    private int _particleMaxArea = DefaultMaxParticleArea;
    private float _particleMinCircularity = DefaultMinCircularity;
    private float _particleMaxCircularity = DefaultMaxCircularity;
    private bool _excludeParticlesOnEdges;
    private bool _showParticlesOverlay = true;

    // ==================== Category 3: 검사 툴 (Inspection Tools) ====================
    private string _selectedNodeName = string.Empty;
    private RoiPreFilterKind _selectedNodePreFilter = RoiPreFilterKind.None;
    private ImageJAutoThresholdMethod _selectedNodeAutoThreshold = ImageJAutoThresholdMethod.Manual;
    private float _selectedNodePatMaxMinScore = 0.50f;
    private float _selectedNodePatMaxAngleDeg = 35.0f;
    private int _selectedNodeBlobMinThreshold = 140;
    private int _selectedNodeBlobMaxThreshold = byte.MaxValue;
    private int _selectedNodeBlobMinArea = 40;
    private int _selectedNodeBinaryMinThreshold = 140;
    private int _selectedNodeBinaryMaxThreshold = byte.MaxValue;
    private float _selectedNodeBinaryMinPassPct = 10.0f;
    private float _selectedNodeBinaryMaxPassPct = 95.0f;
    private CaliperMetroMode _selectedNodeCaliperMode = CaliperMetroMode.SpokeCircleFit;
    private float _selectedNodeCaliperMinContrast = 14.0f;
    private float _selectedNodeCaliperNominalRadius = 18.0f;
    private int _selectedNodeVariationAbsThreshold = 18;
    private int _selectedNodeVariationMinDefectArea = 8;
    private double _selectedNodeJudgeLsl = 1.0;
    private double _selectedNodeJudgeUsl = 500.0;

    private string _inspectionVerdictBadge = "IDLE";
    private string _inspectionVerdictHex = "#475569";
    private string _inspectionSummaryText = "검사 대기 중 · [3. 검사 툴]에서 원하는 검사 알고리즘을 추가하고 [▶ 검사 실행]을 클릭하세요.";

    // ==================== Status & Telemetry Strings ====================
    private string _currentImageInfoText = "열린 이미지 없음 — [📂 이미지 열기 (Ctrl+O)]로 파일을 불러오세요";
    private string _stackStatusText = "0 / 0";
    private string _pixelProbeText = "Cursor (X: -, Y: -) | Value = - GV | 3×3 Mean = - | Sobel |∇I| = -";
    private string _activeRoiSummaryText = "영상을 먼저 열어주세요 (기본 샘플 자동 로드 없음)";
    private string _histogramStatsLine1 = "Count: 0 · Mean: 0.0 ± 0.0 · Min/Max: 0 / 0 · Median (P50): 0 · Mode: 0";
    private string _histogramStatsLine2 = "P5..P95: [0..0] · Skewness: 0.00 · Kurtosis: 0.00 · Entropy: 0.00 bits";
    private string _autoThresholdSummaryText = "Otsu: - | Triangle: - | IsoData: - | MaxEntropy: - | Mean: - | P50: -";
    private string _profileSummaryText = "1D Plot Profile (가로 X-단면) · 영상을 열면 선택 영역 단면 프로파일이 표시됩니다.";
    private string _resultsSummaryText = "ROI 계측 기록 0건 (Ctrl+M으로 현재 ROI 통계 추가)";
    private string _particleSummaryText = "블랍/입자 확인 대기 중 · [🔍 선택 ROI 블랍/입자 확인] 버튼을 누르면 즉시 검출합니다.";
    private string _canvasStatusText = "준비 완료 · 기본 이미지는 로드하지 않습니다. [📂 이미지 열기 (Ctrl+O)]로 ken32.png 등 분석할 영상을 열어주세요.";

    public event PropertyChangedEventHandler? PropertyChanged;

    public MainSimulatorViewModel(
        EditorSession editor,
        InteractionController input,
        ViewportLayout layout,
        ImageCoordinator images,
        ToolBlockDynamicImageLoader loader)
    {
        ArgumentNullException.ThrowIfNull(editor);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(loader);

        _editor = editor;
        _rawInput = input;
        _images = images;
        _loader = loader;
        Input = new ImageJPointerProbeInput(input, OnCanvasPointerMoved);
        Layout = layout;
        KeyAction = HandleKey;

        FilterOptions = BuildFilterOptions();
        _selectedFilterOption = FilterOptions[0];

        InspectionToolPalette = BuildInspectionToolPalette();
        _selectedPaletteTool = InspectionToolPalette[0];

        // File & Stack Commands
        OpenImageFilesCommand = new RelayCommand(() => _ = OpenUserImageFilesDialogAsync());
        SaveImageAsCommand = new RelayCommand(SaveCurrentImageToFileDialog);
        RevertToOriginalCommand = new RelayCommand(RevertActiveSliceToOriginal);
        PixelUndoCommand = new RelayCommand(ExecutePixelUndo, () => _selectedStackFrame?.CanUndo == true);
        PixelRedoCommand = new RelayCommand(ExecutePixelRedo, () => _selectedStackFrame?.CanRedo == true);
        PreviousStackFrameCommand = new RelayCommand(MoveToPreviousStackFrame, () => LoadedImageStack.Count > 1);
        NextStackFrameCommand = new RelayCommand(MoveToNextStackFrame, () => LoadedImageStack.Count > 1);

        // Optional On-Demand Sample Commands (Never loaded automatically on startup!)
        LoadBlobsSampleCommand = new RelayCommand(() => _ = ActivateSampleSliceAsync(ImageJSampleKind.BlobsAndParticles));
        LoadColoniesSampleCommand = new RelayCommand(() => _ = ActivateSampleSliceAsync(ImageJSampleKind.CellColonyCounter));
        LoadMetrologySampleCommand = new RelayCommand(() => _ = ActivateSampleSliceAsync(ImageJSampleKind.MetrologyEdgesAndRings));
        LoadShadingSampleCommand = new RelayCommand(() => _ = ActivateSampleSliceAsync(ImageJSampleKind.UnevenShadingAndSaltPepper));

        // Canvas Tool & ROI Manager Commands
        SelectToolCommand = new RelayCommand(() => _rawInput.Tool = EditorTool.Select);
        PanToolCommand = new RelayCommand(() => _rawInput.Tool = EditorTool.Pan);
        DrawRectCommand = new RelayCommand(() => StartDrawRoi(RoiShapeKind.Rectangle));
        DrawCircleCommand = new RelayCommand(() => StartDrawRoi(RoiShapeKind.Circle));
        DrawPolygonCommand = new RelayCommand(() => StartDrawRoi(RoiShapeKind.Polygon));
        SelectWholeImageRoiCommand = new RelayCommand(SelectWholeImageRoi);
        DeleteRoiCommand = new RelayCommand(DeleteSelectedRoi);
        ClearAllRoisCommand = new RelayCommand(ClearAllCanvasRois);
        FitCommand = new RelayCommand(FitViewport);
        ZoomInCommand = new RelayCommand(() => ZoomViewport(ZoomInStepFactor));
        ZoomOutCommand = new RelayCommand(() => ZoomViewport(ZoomOutStepFactor));

        // ==================== 1. 필터 툴 (Filter Tools) Commands ====================
        AutoContrastCommand = new RelayCommand(ExecuteAutoContrastStretch);
        ResetContrastCommand = new RelayCommand(ResetContrastParameters);
        ApplyContrastToPixelsCommand = new RelayCommand(CommitContrastLutToPixels);

        ApplyOtsuThresholdCommand = new RelayCommand(() => SelectAndApplyAutoThreshold(ImageJAutoThresholdMethod.Otsu, _lastOtsuThreshold));
        ApplyTriangleThresholdCommand = new RelayCommand(() => SelectAndApplyAutoThreshold(ImageJAutoThresholdMethod.Triangle, _lastTriangleThreshold));
        ApplyIsoDataThresholdCommand = new RelayCommand(() => SelectAndApplyAutoThreshold(ImageJAutoThresholdMethod.IsoData, _lastIsoDataThreshold));
        ApplyMaxEntropyThresholdCommand = new RelayCommand(() => SelectAndApplyAutoThreshold(ImageJAutoThresholdMethod.MaxEntropy, _lastMaxEntropyThreshold));
        ApplyMeanThresholdCommand = new RelayCommand(() => SelectAndApplyAutoThreshold(ImageJAutoThresholdMethod.Mean, _lastMeanThreshold));
        ApplyMedianThresholdCommand = new RelayCommand(() => SelectAndApplyAutoThreshold(ImageJAutoThresholdMethod.Percentile, _lastMedianThreshold));
        ApplyThresholdToPixelsCommand = new RelayCommand(CommitThresholdMaskToPixels);

        ApplyFilterToRoiCommand = new RelayCommand(() => ApplySpecificFilterToPixels(_selectedFilterOption.FilterKind, wholeImage: false));
        ApplyFilterToWholeImageCommand = new RelayCommand(() => ApplySpecificFilterToPixels(_selectedFilterOption.FilterKind, wholeImage: true));
        QuickGaussianCommand = new RelayCommand(() => SelectAndPreviewFilter(ImageJFilterKind.GaussianBlur3x3));
        QuickMedianCommand = new RelayCommand(() => SelectAndPreviewFilter(ImageJFilterKind.Median3x3));
        QuickUnsharpCommand = new RelayCommand(() => SelectAndPreviewFilter(ImageJFilterKind.UnsharpSharpen3x3));
        QuickSobelCommand = new RelayCommand(() => SelectAndPreviewFilter(ImageJFilterKind.SobelEdgeMagnitude));
        QuickTopHatCommand = new RelayCommand(() => SelectAndPreviewFilter(ImageJFilterKind.RollingBallTopHat));
        QuickOpenCommand = new RelayCommand(() => SelectAndPreviewFilter(ImageJFilterKind.MorphOpen3x3));
        QuickCloseCommand = new RelayCommand(() => SelectAndPreviewFilter(ImageJFilterKind.MorphClose3x3));
        QuickEqualizeCommand = new RelayCommand(() => SelectAndPreviewFilter(ImageJFilterKind.HistogramEqualize));

        FillRoiCommand = new RelayCommand(ApplyFillToActiveRoi);
        ClearOutsideRoiCommand = new RelayCommand(ApplyClearOutsideActiveRoi);
        InvertRoiCommand = new RelayCommand(() => ApplySpecificFilterToPixels(ImageJFilterKind.Invert, wholeImage: false));

        // ==================== 2. 확인 툴 (Check / Verify Tools) Commands ====================
        RefreshCheckChartsCommand = new RelayCommand(RefreshHistogramAndProfileCharts);
        ToggleProfileDirectionCommand = new RelayCommand(() => IsVerticalProfileMode = !IsVerticalProfileMode);
        RunAnalyzeParticlesCommand = new RelayCommand(ExecuteAnalyzeParticlesOnDemand);
        ClearParticlesOverlayCommand = new RelayCommand(ClearParticlesOverlay);
        MeasureRoiCommand = new RelayCommand(MeasureActiveRoiIntoTable);
        MultiMeasureAllRoisCommand = new RelayCommand(MultiMeasureAllRoisIntoTable);
        ClearMeasurementsCommand = new RelayCommand(ClearMeasurementsTable);
        ExportMeasurementsCsvCommand = new RelayCommand(ExportMeasurementsToCsvDialog);

        // ==================== 3. 검사 툴 (Inspection Tools) Commands ====================
        AddInspectionToolCommand = new RelayCommand(AddSelectedPaletteToolToPipeline);
        BindSelectedRoiToNodeCommand = new RelayCommand(BindActiveCanvasRoiToSelectedNode);
        SyncFilterThresholdToNodeCommand = new RelayCommand(CopyActiveThresholdToSelectedNode);
        RemoveSelectedInspectionToolCommand = new RelayCommand(RemoveSelectedInspectionNode);
        ClearInspectionPipelineCommand = new RelayCommand(ClearAllInspectionNodes);
        RunInspectionPipelineCommand = new RelayCommand(ExecuteInspectionPipelineNow);

        _editor.Changed += OnEditorChanged;
        _rawInput.Changed += PublishCanvasScene;
        Layout.Changed += PublishCanvasScene;
        _images.Changed += PublishCanvasScene;
        _images.Status += msg => CanvasStatusText = msg;

        // Publish empty startup scene (zero sample images loaded on startup!)
        PublishCanvasScene();
    }

    // VisionCanvas Bindings
    public IViewportInput Input { get; }
    public ViewportLayout Layout { get; }
    public Func<Key, bool, bool, bool> KeyAction { get; }
    public RenderScene Scene { get; private set; } = null!;
    public bool IsNavigationEnabled => !_disposed && _rawInput.IsEnabled;

    // Observable Collections for UI
    public ObservableCollection<LoadedImageFrameItem> LoadedImageStack { get; } = [];
    public ObservableCollection<CanvasRoiOptionItem> CanvasRoiOptions { get; } = [];
    public IReadOnlyList<ImageJFilterOptionItem> FilterOptions { get; }
    public IReadOnlyList<ImageJAutoThresholdMethod> AutoThresholdMethods { get; } = Enum.GetValues<ImageJAutoThresholdMethod>();
    public IReadOnlyList<RoiPreFilterKind> NodePreFilterOptions { get; } = Enum.GetValues<RoiPreFilterKind>();
    public IReadOnlyList<CaliperMetroMode> CaliperModeOptions { get; } = Enum.GetValues<CaliperMetroMode>();
    public IReadOnlyList<ToolboxPaletteItem> InspectionToolPalette { get; }

    public ObservableCollection<HistogramBarDisplayItem> HistogramBars { get; } = [];
    public ObservableCollection<ProfileBarDisplayItem> ProfileBars { get; } = [];
    public ObservableCollection<ImageJParticleRecord> ParticleRows { get; } = [];
    public ObservableCollection<ImageJMeasurementRecord> MeasuredRows { get; } = [];

    public ObservableCollection<DagNodeDisplayItem> InspectionNodeRows { get; } = [];
    public ObservableCollection<AlgorithmResultSummaryRow> InspectionResultRows { get; } = [];

    // Commands — File & Stack
    public ICommand OpenImageFilesCommand { get; }
    public ICommand SaveImageAsCommand { get; }
    public ICommand RevertToOriginalCommand { get; }
    public ICommand PixelUndoCommand { get; }
    public ICommand PixelRedoCommand { get; }
    public ICommand PreviousStackFrameCommand { get; }
    public ICommand NextStackFrameCommand { get; }

    public ICommand LoadBlobsSampleCommand { get; }
    public ICommand LoadColoniesSampleCommand { get; }
    public ICommand LoadMetrologySampleCommand { get; }
    public ICommand LoadShadingSampleCommand { get; }

    // Commands — Selections & ROI Manager
    public ICommand SelectToolCommand { get; }
    public ICommand PanToolCommand { get; }
    public ICommand DrawRectCommand { get; }
    public ICommand DrawCircleCommand { get; }
    public ICommand DrawPolygonCommand { get; }
    public ICommand SelectWholeImageRoiCommand { get; }
    public ICommand DeleteRoiCommand { get; }
    public ICommand ClearAllRoisCommand { get; }
    public ICommand FitCommand { get; }
    public ICommand ZoomInCommand { get; }
    public ICommand ZoomOutCommand { get; }

    // Commands — 1. 필터 툴 (Filter Tools)
    public ICommand AutoContrastCommand { get; }
    public ICommand ResetContrastCommand { get; }
    public ICommand ApplyContrastToPixelsCommand { get; }

    public ICommand ApplyOtsuThresholdCommand { get; }
    public ICommand ApplyTriangleThresholdCommand { get; }
    public ICommand ApplyIsoDataThresholdCommand { get; }
    public ICommand ApplyMaxEntropyThresholdCommand { get; }
    public ICommand ApplyMeanThresholdCommand { get; }
    public ICommand ApplyMedianThresholdCommand { get; }
    public ICommand ApplyThresholdToPixelsCommand { get; }

    public ICommand ApplyFilterToRoiCommand { get; }
    public ICommand ApplyFilterToWholeImageCommand { get; }
    public ICommand QuickGaussianCommand { get; }
    public ICommand QuickMedianCommand { get; }
    public ICommand QuickUnsharpCommand { get; }
    public ICommand QuickSobelCommand { get; }
    public ICommand QuickTopHatCommand { get; }
    public ICommand QuickOpenCommand { get; }
    public ICommand QuickCloseCommand { get; }
    public ICommand QuickEqualizeCommand { get; }

    public ICommand FillRoiCommand { get; }
    public ICommand ClearOutsideRoiCommand { get; }
    public ICommand InvertRoiCommand { get; }

    // Commands — 2. 확인 툴 (Check / Verify Tools)
    public ICommand RefreshCheckChartsCommand { get; }
    public ICommand ToggleProfileDirectionCommand { get; }
    public ICommand RunAnalyzeParticlesCommand { get; }
    public ICommand ClearParticlesOverlayCommand { get; }
    public ICommand MeasureRoiCommand { get; }
    public ICommand MultiMeasureAllRoisCommand { get; }
    public ICommand ClearMeasurementsCommand { get; }
    public ICommand ExportMeasurementsCsvCommand { get; }

    // Commands — 3. 검사 툴 (Inspection Tools)
    public ICommand AddInspectionToolCommand { get; }
    public ICommand BindSelectedRoiToNodeCommand { get; }
    public ICommand SyncFilterThresholdToNodeCommand { get; }
    public ICommand RemoveSelectedInspectionToolCommand { get; }
    public ICommand ClearInspectionPipelineCommand { get; }
    public ICommand RunInspectionPipelineCommand { get; }

    // ==================== Bindable Properties ====================
    public int SelectedToolCategoryIndex
    {
        get => _selectedToolCategoryIndex;
        set
        {
            if (SetField(ref _selectedToolCategoryIndex, value))
            {
                if (value == 1 && SelectedBottomTabIndex == 4)
                {
                    SelectedBottomTabIndex = 0;
                }
                else if (value == 2)
                {
                    SelectedBottomTabIndex = 4;
                }
            }
        }
    }

    public int SelectedBottomTabIndex
    {
        get => _selectedBottomTabIndex;
        set => SetField(ref _selectedBottomTabIndex, value);
    }

    public LoadedImageFrameItem? SelectedStackFrame
    {
        get => _selectedStackFrame;
        set
        {
            if (SetField(ref _selectedStackFrame, value) && value is not null && !_suppressEvents)
            {
                _ = ActivateLoadedFrameAsync(value, resetRois: false);
            }
        }
    }

    public CanvasRoiOptionItem? SelectedRoiOption
    {
        get => _selectedRoiOption;
        set
        {
            if (SetField(ref _selectedRoiOption, value) && !_suppressEvents && value is not null)
            {
                if (value.RoiId == Guid.Empty)
                {
                    _editor.Select([]);
                }
                else if (_editor.State.Rois.ContainsKey(value.RoiId))
                {
                    _editor.Select([value.RoiId]);
                }

                UpdateFilterPreviewAndCheckCharts();
                PublishCanvasScene();
            }
        }
    }

    public ImageJFilterOptionItem SelectedFilterOption
    {
        get => _selectedFilterOption;
        set
        {
            if (SetField(ref _selectedFilterOption, value) && _isLiveFilterPreviewEnabled)
            {
                UpdateFilterPreviewAndCheckCharts();
                PublishCanvasScene();
            }
        }
    }

    public ToolboxPaletteItem SelectedPaletteTool
    {
        get => _selectedPaletteTool;
        set => SetField(ref _selectedPaletteTool, value);
    }

    public DagNodeDisplayItem? SelectedInspectionNode
    {
        get => _selectedInspectionNode;
        set
        {
            if (SetField(ref _selectedInspectionNode, value))
            {
                LoadSelectedNodeParametersIntoEditor();
                if (value?.BoundRoiId is { } roiId && _editor.State.Rois.ContainsKey(roiId))
                {
                    _editor.Select([roiId]);
                }
            }
        }
    }

    // Category 1 Properties: Brightness/Contrast, Threshold, Filters
    public int ContrastDisplayMin
    {
        get => _contrastDisplayMin;
        set
        {
            int clamped = Math.Clamp(value, byte.MinValue, byte.MaxValue - 1);
            if (SetField(ref _contrastDisplayMin, clamped))
            {
                if (_contrastDisplayMax <= _contrastDisplayMin)
                {
                    _contrastDisplayMax = Math.Min(byte.MaxValue, _contrastDisplayMin + 1);
                    OnPropertyChanged(nameof(ContrastDisplayMax));
                }

                if (_isLiveContrastPreviewEnabled)
                {
                    UpdateFilterPreviewAndCheckCharts();
                    PublishCanvasScene();
                }
            }
        }
    }

    public int ContrastDisplayMax
    {
        get => _contrastDisplayMax;
        set
        {
            int clamped = Math.Clamp(value, 1, byte.MaxValue);
            if (SetField(ref _contrastDisplayMax, clamped))
            {
                if (_contrastDisplayMin >= _contrastDisplayMax)
                {
                    _contrastDisplayMin = Math.Max(byte.MinValue, _contrastDisplayMax - 1);
                    OnPropertyChanged(nameof(ContrastDisplayMin));
                }

                if (_isLiveContrastPreviewEnabled)
                {
                    UpdateFilterPreviewAndCheckCharts();
                    PublishCanvasScene();
                }
            }
        }
    }

    public float ContrastGamma
    {
        get => _contrastGamma;
        set
        {
            float clamped = Math.Clamp(value, ImageJContrastSpec.MinAllowedGamma, ImageJContrastSpec.MaxAllowedGamma);
            if (SetField(ref _contrastGamma, clamped) && _isLiveContrastPreviewEnabled)
            {
                UpdateFilterPreviewAndCheckCharts();
                PublishCanvasScene();
            }
        }
    }

    public bool IsLiveContrastPreviewEnabled
    {
        get => _isLiveContrastPreviewEnabled;
        set
        {
            if (SetField(ref _isLiveContrastPreviewEnabled, value))
            {
                UpdateFilterPreviewAndCheckCharts();
                PublishCanvasScene();
            }
        }
    }

    public ImageJAutoThresholdMethod SelectedAutoThresholdMethod
    {
        get => _selectedAutoThresholdMethod;
        set
        {
            if (SetField(ref _selectedAutoThresholdMethod, value))
            {
                ApplySelectedAutoThresholdMethod();
            }
        }
    }

    public int ThresholdMin
    {
        get => _thresholdMin;
        set
        {
            int clamped = Math.Clamp(value, byte.MinValue, byte.MaxValue);
            if (SetField(ref _thresholdMin, clamped))
            {
                if (_thresholdMax < _thresholdMin)
                {
                    _thresholdMax = _thresholdMin;
                    OnPropertyChanged(nameof(ThresholdMax));
                }

                UpdateFilterPreviewAndCheckCharts();
                PublishCanvasScene();
            }
        }
    }

    public int ThresholdMax
    {
        get => _thresholdMax;
        set
        {
            int clamped = Math.Clamp(value, byte.MinValue, byte.MaxValue);
            if (SetField(ref _thresholdMax, clamped))
            {
                if (_thresholdMin > _thresholdMax)
                {
                    _thresholdMin = _thresholdMax;
                    OnPropertyChanged(nameof(ThresholdMin));
                }

                UpdateFilterPreviewAndCheckCharts();
                PublishCanvasScene();
            }
        }
    }

    public bool IsDarkBackground
    {
        get => _isDarkBackground;
        set
        {
            if (SetField(ref _isDarkBackground, value))
            {
                ApplySelectedAutoThresholdMethod();
                UpdateFilterPreviewAndCheckCharts();
                PublishCanvasScene();
            }
        }
    }

    public bool IsLiveThresholdPreviewEnabled
    {
        get => _isLiveThresholdPreviewEnabled;
        set
        {
            if (SetField(ref _isLiveThresholdPreviewEnabled, value))
            {
                UpdateFilterPreviewAndCheckCharts();
                PublishCanvasScene();
            }
        }
    }

    public bool IsLiveFilterPreviewEnabled
    {
        get => _isLiveFilterPreviewEnabled;
        set
        {
            if (SetField(ref _isLiveFilterPreviewEnabled, value))
            {
                UpdateFilterPreviewAndCheckCharts();
                PublishCanvasScene();
            }
        }
    }

    public int FillGrayValue
    {
        get => _fillGrayValue;
        set => SetField(ref _fillGrayValue, Math.Clamp(value, byte.MinValue, byte.MaxValue));
    }

    // Category 2 Properties: Check / Verify Tools
    public bool IsVerticalProfileMode
    {
        get => _isVerticalProfileMode;
        set
        {
            if (SetField(ref _isVerticalProfileMode, value))
            {
                RefreshHistogramAndProfileCharts();
            }
        }
    }

    public int ParticleMinArea
    {
        get => _particleMinArea;
        set => SetField(ref _particleMinArea, Math.Max(1, value));
    }

    public int ParticleMaxArea
    {
        get => _particleMaxArea;
        set => SetField(ref _particleMaxArea, Math.Max(_particleMinArea, value));
    }

    public float ParticleMinCircularity
    {
        get => _particleMinCircularity;
        set => SetField(ref _particleMinCircularity, Math.Clamp(value, 0.0f, 1.0f));
    }

    public float ParticleMaxCircularity
    {
        get => _particleMaxCircularity;
        set => SetField(ref _particleMaxCircularity, Math.Clamp(value, _particleMinCircularity, 1.0f));
    }

    public bool ExcludeParticlesOnEdges
    {
        get => _excludeParticlesOnEdges;
        set => SetField(ref _excludeParticlesOnEdges, value);
    }

    public bool ShowParticlesOverlay
    {
        get => _showParticlesOverlay;
        set
        {
            if (SetField(ref _showParticlesOverlay, value))
            {
                PublishCanvasScene();
            }
        }
    }

    // Category 3 Properties: Selected Inspection Tool Parameters
    public string SelectedNodeName
    {
        get => _selectedNodeName;
        set
        {
            if (SetField(ref _selectedNodeName, value))
            {
                UpdateSelectedNodeSpecInList();
            }
        }
    }

    public RoiPreFilterKind SelectedNodePreFilter
    {
        get => _selectedNodePreFilter;
        set
        {
            if (SetField(ref _selectedNodePreFilter, value))
            {
                UpdateSelectedNodeSpecInList();
            }
        }
    }

    public ImageJAutoThresholdMethod SelectedNodeAutoThreshold
    {
        get => _selectedNodeAutoThreshold;
        set
        {
            if (SetField(ref _selectedNodeAutoThreshold, value))
            {
                UpdateSelectedNodeSpecInList();
            }
        }
    }

    public float SelectedNodePatMaxMinScore
    {
        get => _selectedNodePatMaxMinScore;
        set
        {
            if (SetField(ref _selectedNodePatMaxMinScore, Math.Clamp(value, 0.15f, 0.99f)))
            {
                UpdateSelectedNodeSpecInList();
            }
        }
    }

    public float SelectedNodePatMaxAngleDeg
    {
        get => _selectedNodePatMaxAngleDeg;
        set
        {
            if (SetField(ref _selectedNodePatMaxAngleDeg, Math.Clamp(value, 1.0f, 180.0f)))
            {
                UpdateSelectedNodeSpecInList();
            }
        }
    }

    public int SelectedNodeBlobMinThreshold
    {
        get => _selectedNodeBlobMinThreshold;
        set
        {
            if (SetField(ref _selectedNodeBlobMinThreshold, Math.Clamp(value, 0, 255)))
            {
                UpdateSelectedNodeSpecInList();
            }
        }
    }

    public int SelectedNodeBlobMaxThreshold
    {
        get => _selectedNodeBlobMaxThreshold;
        set
        {
            if (SetField(ref _selectedNodeBlobMaxThreshold, Math.Clamp(value, 0, 255)))
            {
                UpdateSelectedNodeSpecInList();
            }
        }
    }

    public int SelectedNodeBlobMinArea
    {
        get => _selectedNodeBlobMinArea;
        set
        {
            if (SetField(ref _selectedNodeBlobMinArea, Math.Max(1, value)))
            {
                UpdateSelectedNodeSpecInList();
            }
        }
    }

    public int SelectedNodeBinaryMinThreshold
    {
        get => _selectedNodeBinaryMinThreshold;
        set
        {
            if (SetField(ref _selectedNodeBinaryMinThreshold, Math.Clamp(value, 0, 255)))
            {
                UpdateSelectedNodeSpecInList();
            }
        }
    }

    public int SelectedNodeBinaryMaxThreshold
    {
        get => _selectedNodeBinaryMaxThreshold;
        set
        {
            if (SetField(ref _selectedNodeBinaryMaxThreshold, Math.Clamp(value, 0, 255)))
            {
                UpdateSelectedNodeSpecInList();
            }
        }
    }

    public float SelectedNodeBinaryMinPassPct
    {
        get => _selectedNodeBinaryMinPassPct;
        set
        {
            if (SetField(ref _selectedNodeBinaryMinPassPct, Math.Clamp(value, 0.0f, 100.0f)))
            {
                UpdateSelectedNodeSpecInList();
            }
        }
    }

    public float SelectedNodeBinaryMaxPassPct
    {
        get => _selectedNodeBinaryMaxPassPct;
        set
        {
            if (SetField(ref _selectedNodeBinaryMaxPassPct, Math.Clamp(value, 0.0f, 100.0f)))
            {
                UpdateSelectedNodeSpecInList();
            }
        }
    }

    public CaliperMetroMode SelectedNodeCaliperMode
    {
        get => _selectedNodeCaliperMode;
        set
        {
            if (SetField(ref _selectedNodeCaliperMode, value))
            {
                UpdateSelectedNodeSpecInList();
            }
        }
    }

    public float SelectedNodeCaliperMinContrast
    {
        get => _selectedNodeCaliperMinContrast;
        set
        {
            if (SetField(ref _selectedNodeCaliperMinContrast, Math.Clamp(value, 2.0f, 180.0f)))
            {
                UpdateSelectedNodeSpecInList();
            }
        }
    }

    public float SelectedNodeCaliperNominalRadius
    {
        get => _selectedNodeCaliperNominalRadius;
        set
        {
            if (SetField(ref _selectedNodeCaliperNominalRadius, Math.Max(4.0f, value)))
            {
                UpdateSelectedNodeSpecInList();
            }
        }
    }

    public int SelectedNodeVariationAbsThreshold
    {
        get => _selectedNodeVariationAbsThreshold;
        set
        {
            if (SetField(ref _selectedNodeVariationAbsThreshold, Math.Clamp(value, 2, 200)))
            {
                UpdateSelectedNodeSpecInList();
            }
        }
    }

    public int SelectedNodeVariationMinDefectArea
    {
        get => _selectedNodeVariationMinDefectArea;
        set
        {
            if (SetField(ref _selectedNodeVariationMinDefectArea, Math.Max(1, value)))
            {
                UpdateSelectedNodeSpecInList();
            }
        }
    }

    public double SelectedNodeJudgeLsl
    {
        get => _selectedNodeJudgeLsl;
        set
        {
            if (SetField(ref _selectedNodeJudgeLsl, value))
            {
                UpdateSelectedNodeSpecInList();
            }
        }
    }

    public double SelectedNodeJudgeUsl
    {
        get => _selectedNodeJudgeUsl;
        set
        {
            if (SetField(ref _selectedNodeJudgeUsl, value))
            {
                UpdateSelectedNodeSpecInList();
            }
        }
    }

    public string InspectionVerdictBadge
    {
        get => _inspectionVerdictBadge;
        private set => SetField(ref _inspectionVerdictBadge, value);
    }

    public string InspectionVerdictHex
    {
        get => _inspectionVerdictHex;
        private set => SetField(ref _inspectionVerdictHex, value);
    }

    public string InspectionSummaryText
    {
        get => _inspectionSummaryText;
        private set => SetField(ref _inspectionSummaryText, value);
    }

    // Status Text Properties
    public string CurrentImageInfoText
    {
        get => _currentImageInfoText;
        private set => SetField(ref _currentImageInfoText, value);
    }

    public string StackStatusText
    {
        get => _stackStatusText;
        private set => SetField(ref _stackStatusText, value);
    }

    public string PixelProbeText
    {
        get => _pixelProbeText;
        private set => SetField(ref _pixelProbeText, value);
    }

    public string ActiveRoiSummaryText
    {
        get => _activeRoiSummaryText;
        private set => SetField(ref _activeRoiSummaryText, value);
    }

    public string HistogramStatsLine1
    {
        get => _histogramStatsLine1;
        private set => SetField(ref _histogramStatsLine1, value);
    }

    public string HistogramStatsLine2
    {
        get => _histogramStatsLine2;
        private set => SetField(ref _histogramStatsLine2, value);
    }

    public string AutoThresholdSummaryText
    {
        get => _autoThresholdSummaryText;
        private set => SetField(ref _autoThresholdSummaryText, value);
    }

    public string ProfileSummaryText
    {
        get => _profileSummaryText;
        private set => SetField(ref _profileSummaryText, value);
    }

    public string ResultsSummaryText
    {
        get => _resultsSummaryText;
        private set => SetField(ref _resultsSummaryText, value);
    }

    public string ParticleSummaryText
    {
        get => _particleSummaryText;
        private set => SetField(ref _particleSummaryText, value);
    }

    public string CanvasStatusText
    {
        get => _canvasStatusText;
        private set => SetField(ref _canvasStatusText, value);
    }

    public bool HandleKey(Key key, bool control, bool shift)
    {
        if (_disposed)
        {
            return false;
        }

        if (control && key == Key.O)
        {
            _ = OpenUserImageFilesDialogAsync();
            return true;
        }

        if (control && key == Key.S)
        {
            SaveCurrentImageToFileDialog();
            return true;
        }

        if (control && key == Key.M)
        {
            MeasureActiveRoiIntoTable();
            return true;
        }

        if (control && key == Key.H)
        {
            SelectedToolCategoryIndex = 1;
            SelectedBottomTabIndex = 0;
            RefreshHistogramAndProfileCharts();
            return true;
        }

        if (control && key == Key.K)
        {
            SelectedToolCategoryIndex = 1;
            SelectedBottomTabIndex = 1;
            RefreshHistogramAndProfileCharts();
            return true;
        }

        if (control && shift && key == Key.T)
        {
            SelectedToolCategoryIndex = 0;
            IsLiveThresholdPreviewEnabled = !IsLiveThresholdPreviewEnabled;
            return true;
        }

        if (key == Key.F5)
        {
            SelectedToolCategoryIndex = 2;
            ExecuteInspectionPipelineNow();
            return true;
        }

        if (control && key == Key.Z)
        {
            ExecutePixelUndo();
            return true;
        }

        if (control && key == Key.Y)
        {
            ExecutePixelRedo();
            return true;
        }

        if (!control && key == Key.Delete)
        {
            DeleteSelectedRoi();
            return true;
        }

        if (!control && key == Key.F)
        {
            FitViewport();
            return true;
        }

        if (!control && key == Key.Escape)
        {
            _rawInput.Cancel();
            return true;
        }

        return ViewportKeyboard.Nudge(Input, key, control, shift);
    }

    // ==================== File & Zero-Full-Allocation Disk Tile Loading ====================
    private async Task OpenUserImageFilesDialogAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "분석/검사할 이미지 열기 (ken32.png 등 대용량 영상 즉시 타일 스트리밍 지원)",
            Filter = "Image Files (*.png;*.bmp;*.jpg;*.jpeg;*.tif;*.tiff)|*.png;*.bmp;*.jpg;*.jpeg;*.tif;*.tiff|All Files (*.*)|*.*",
            Multiselect = true
        };

        if (dialog.ShowDialog() != true || dialog.FileNames.Length == 0)
        {
            return;
        }

        _suppressEvents = true;
        try
        {
            LoadedImageStack.Clear();
            for (int i = 0; i < dialog.FileNames.Length; i++)
            {
                string file = dialog.FileNames[i];
                if (File.Exists(file))
                {
                    // Zero full-image allocation: register as DiskTiled so VisionCanvas streams 512x512 pyramid tiles!
                    LoadedImageStack.Add(LoadedImageFrameItem.CreateDiskTiled(LoadedImageStack.Count, file));
                }
            }
        }
        finally
        {
            _suppressEvents = false;
        }

        if (LoadedImageStack.Count == 0)
        {
            return;
        }

        _suppressEvents = true;
        _selectedStackFrame = LoadedImageStack[0];
        OnPropertyChanged(nameof(SelectedStackFrame));
        _suppressEvents = false;

        CommandManager.InvalidateRequerySuggested();
        await ActivateLoadedFrameAsync(LoadedImageStack[0], resetRois: true).ConfigureAwait(true);
    }

    private async Task ActivateSampleSliceAsync(ImageJSampleKind kind)
    {
        for (int i = 0; i < LoadedImageStack.Count; i++)
        {
            if (!LoadedImageStack[i].IsDiskTiled &&
                LoadedImageStack[i].FullPath.EndsWith(kind.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                SelectedStackFrame = LoadedImageStack[i];
                return;
            }
        }

        LoadedImageFrameItem sampleFrame = ToolBlockDynamicImageLoader.CreateSampleFrame(LoadedImageStack.Count, kind);
        LoadedImageStack.Add(sampleFrame);
        SelectedStackFrame = sampleFrame;
        await Task.CompletedTask.ConfigureAwait(true);
    }

    private async Task ActivateLoadedFrameAsync(LoadedImageFrameItem frame, bool resetRois)
    {
        _activeFilterPreview = null;
        _particlesOverlay = null;
        _inspectionOverlay = null;

        if (frame.IsDiskTiled)
        {
            // Stream directly through VisionCanvas ImageCoordinator.ReplaceAsync (TrianglePyramid disk cache)
            var diskRef = new ImageReference(frame.FullPath, frame.FileName, 0, 0, frame.DiskVersion);
            await _images.ReplaceAsync(diskRef).ConfigureAwait(true);

            if (_images.Image is { } resolvedInfo)
            {
                frame.UpdateResolvedDimensions(resolvedInfo.Width, resolvedInfo.Height);
                _activeWidth = resolvedInfo.Width;
                _activeHeight = resolvedInfo.Height;
                _activeStride = resolvedInfo.Width;
                _loader.BindDiskAnalysisSource(frame.FullPath, resolvedInfo.Width, resolvedInfo.Height);
            }

            if (resetRois || _editor.State.Rois.Count == 0)
            {
                EnsureDefaultWorkingRoi();
            }
        }
        else
        {
            bool sizeChanged = frame.Width != _activeWidth || frame.Height != _activeHeight;
            _activeWidth = frame.Width;
            _activeHeight = frame.Height;
            _activeStride = frame.Stride;

            _loader.UpdateActiveBuffer(frame.WorkingPixels, _activeWidth, _activeHeight, _activeStride);

            var imageRef = new ImageReference(
                $"{ToolBlockDynamicImageLoader.LiveKeyPrefix}{_liveFrameCounter++}",
                frame.FileName,
                _activeWidth,
                _activeHeight);

            if (resetRois || sizeChanged || _editor.State.Rois.Count == 0)
            {
                Roi initialRoi = CreateDefaultCenteredRoi(_activeWidth, _activeHeight);
                await _images.LoadDocumentAsync(imageRef, [initialRoi]).ConfigureAwait(true);
                FitViewport();
            }
            else
            {
                List<Roi> preservedRois = _editor.State.Rois.Values.OrderBy(r => r.Order).ToList();
                await _images.LoadDocumentAsync(imageRef, preservedRois).ConfigureAwait(true);
            }
        }

        CurrentImageInfoText = frame.IsDiskTiled
            ? $"{frame.FileName} ({_activeWidth:N0}×{_activeHeight:N0} · Disk Pyramid Streaming)"
            : $"{frame.FileName} ({_activeWidth:N0}×{_activeHeight:N0} Gray8)";
        StackStatusText = $"Slice {frame.FrameIndex + 1} / {Math.Max(1, LoadedImageStack.Count)}";

        RebuildCanvasRoiOptionsList();
        UpdateFilterPreviewAndCheckCharts();
        ApplySelectedAutoThresholdMethod();
        PublishCanvasScene();
        CommandManager.InvalidateRequerySuggested();
    }

    private void EnsureDefaultWorkingRoi()
    {
        if (_activeWidth <= 0 || _activeHeight <= 0)
        {
            return;
        }

        // For ultra-large images like ken32.png (17,280 x 30,720), create a practical 1024x1024 initial ROI at the center
        // so the user can immediately drag/inspect at 1:1 Level-0 resolution without covering 530 MP by default.
        double rw = Math.Clamp(Math.Min(_activeWidth * 0.25, 1024.0), 64.0, Math.Max(64.0, _activeWidth - 8.0));
        double rh = Math.Clamp(Math.Min(_activeHeight * 0.25, 1024.0), 64.0, Math.Max(64.0, _activeHeight - 8.0));
        var geometry = new RoiGeometry(new PointD(_activeWidth * 0.5, _activeHeight * 0.5), rw, rh, 0.0);

        _suppressEvents = true;
        try
        {
            _editor.Add(geometry);
        }
        finally
        {
            _suppressEvents = false;
        }
    }

    private static Roi CreateDefaultCenteredRoi(int width, int height)
    {
        double rw = Math.Clamp(width * 0.65, 32, Math.Max(32, width - 8));
        double rh = Math.Clamp(height * 0.65, 32, Math.Max(32, height - 8));
        return new Roi(
            Guid.NewGuid(),
            "ROI 001",
            new RoiGeometry(new PointD(width * 0.5, height * 0.5), rw, rh, 0.0),
            ParentId: null,
            IsGroup: false,
            Order: 1);
    }

    private void SaveCurrentImageToFileDialog()
    {
        if (_selectedStackFrame is null || _activeWidth <= 0 || _activeHeight <= 0)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "현재 선택 영역(또는 영상) Gray8 저장 (Save Image As...)",
            FileName = $"Processed_{Path.GetFileNameWithoutExtension(_selectedStackFrame.FileName)}.png",
            Filter = "PNG Image (*.png)|*.png|BMP Image (*.bmp)|*.bmp|TIFF Image (*.tif)|*.tif|JPEG Image (*.jpg)|*.jpg"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var (_, _, _, rx, ry, rw, rh, _) = GetActiveRoiBounds();
            ExtractedRoiWindow win = GetProcessedActiveRoiWindow(rx, ry, rw, rh, MaxInspectionWindowPixels);
            if (!win.IsValid)
            {
                return;
            }

            ToolBlockDynamicImageLoader.SaveGray8ImageToFile(
                dialog.FileName,
                win.Pixels,
                win.Width,
                win.Height,
                win.Stride);
            CanvasStatusText = $"영상을 저장했습니다: {dialog.FileName} ({win.Width}×{win.Height} px)";
        }
        catch (Exception ex)
        {
            CanvasStatusText = $"영상 저장 실패: {ex.Message}";
        }
    }

    private void MoveToPreviousStackFrame()
    {
        if (LoadedImageStack.Count <= 1 || _selectedStackFrame is null)
        {
            return;
        }

        int idx = LoadedImageStack.IndexOf(_selectedStackFrame);
        int prevIdx = idx <= 0 ? LoadedImageStack.Count - 1 : idx - 1;
        SelectedStackFrame = LoadedImageStack[prevIdx];
    }

    private void MoveToNextStackFrame()
    {
        if (LoadedImageStack.Count <= 1 || _selectedStackFrame is null)
        {
            return;
        }

        int idx = LoadedImageStack.IndexOf(_selectedStackFrame);
        int nextIdx = (idx + 1) % LoadedImageStack.Count;
        SelectedStackFrame = LoadedImageStack[nextIdx];
    }

    private void RevertActiveSliceToOriginal()
    {
        if (_selectedStackFrame is null)
        {
            return;
        }

        _selectedStackFrame.RevertToOriginal();
        _contrastDisplayMin = byte.MinValue;
        _contrastDisplayMax = byte.MaxValue;
        _contrastGamma = ImageJContrastSpec.NeutralGamma;
        _isLiveContrastPreviewEnabled = false;
        _isLiveThresholdPreviewEnabled = false;
        _isLiveFilterPreviewEnabled = false;
        _activeFilterPreview = null;
        _particlesOverlay = null;
        _inspectionOverlay = null;

        OnPropertyChanged(nameof(ContrastDisplayMin));
        OnPropertyChanged(nameof(ContrastDisplayMax));
        OnPropertyChanged(nameof(ContrastGamma));
        OnPropertyChanged(nameof(IsLiveContrastPreviewEnabled));
        OnPropertyChanged(nameof(IsLiveThresholdPreviewEnabled));
        OnPropertyChanged(nameof(IsLiveFilterPreviewEnabled));

        if (!_selectedStackFrame.IsDiskTiled)
        {
            RefreshInMemoryCanvasDisplay();
        }
        else
        {
            UpdateFilterPreviewAndCheckCharts();
            PublishCanvasScene();
        }

        CommandManager.InvalidateRequerySuggested();
        CanvasStatusText = $"[{_selectedStackFrame.FileName}] 원본 상태로 복원했습니다.";
    }

    private void ExecutePixelUndo()
    {
        if (_selectedStackFrame is null || !_selectedStackFrame.TryUndo())
        {
            return;
        }

        RefreshInMemoryCanvasDisplay();
        CommandManager.InvalidateRequerySuggested();
        CanvasStatusText = $"픽셀 편집 실행취소 (Undo 남은 단계: {_selectedStackFrame.UndoDepth}).";
    }

    private void ExecutePixelRedo()
    {
        if (_selectedStackFrame is null || !_selectedStackFrame.TryRedo())
        {
            return;
        }

        RefreshInMemoryCanvasDisplay();
        CommandManager.InvalidateRequerySuggested();
        CanvasStatusText = $"픽셀 편집 다시실행 (Redo 남은 단계: {_selectedStackFrame.RedoDepth}).";
    }

    // ==================== ROI Drawing & Navigation ====================
    private void StartDrawRoi(RoiShapeKind shapeKind)
    {
        if (_selectedStackFrame is null)
        {
            CanvasStatusText = "먼저 [📂 이미지 열기 (Ctrl+O)]로 영상을 불러온 뒤 ROI를 그리세요.";
            return;
        }

        _rawInput.DrawAsGroup = false;
        _rawInput.DrawParentId = null;
        _rawInput.DrawShape = shapeKind;
        _rawInput.Tool = EditorTool.Draw;
        CanvasStatusText = $"캔버스에 마우스를 드래그하여 [{shapeKind}] ROI 영역을 그리세요.";
    }

    private void SelectWholeImageRoi()
    {
        if (_selectedStackFrame is null)
        {
            return;
        }

        _editor.Select([]);
        if (CanvasRoiOptions.Count > 0)
        {
            _suppressEvents = true;
            _selectedRoiOption = CanvasRoiOptions[0];
            OnPropertyChanged(nameof(SelectedRoiOption));
            _suppressEvents = false;
        }

        UpdateFilterPreviewAndCheckCharts();
        PublishCanvasScene();
        CanvasStatusText = $"전체 영상 영역 (0, 0, {_activeWidth:N0}×{_activeHeight:N0})이 선택되었습니다.";
    }

    private void DeleteSelectedRoi()
    {
        _editor.DeleteSelection();
        RebuildCanvasRoiOptionsList();
        UpdateFilterPreviewAndCheckCharts();
        PublishCanvasScene();
    }

    private void ClearAllCanvasRois()
    {
        _editor.SelectAll();
        _editor.DeleteSelection();
        RebuildCanvasRoiOptionsList();
        UpdateFilterPreviewAndCheckCharts();
        PublishCanvasScene();
        CanvasStatusText = "캔버스의 모든 ROI를 삭제했습니다.";
    }

    private void FitViewport()
    {
        if (_editor.State.Image is { } img)
        {
            _editor.SetViewport(ViewportTransform.Fit(img.Width, img.Height, Layout.Width, Layout.Height));
        }
    }

    private void ZoomViewport(double factor)
    {
        var anchor = new PointD(Layout.Width * 0.5, Layout.Height * 0.5);
        _editor.SetViewport(_editor.State.Viewport.ZoomAt(anchor, factor));
    }

    private void OnEditorChanged(object? sender, EventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }

        // CRITICAL FOR 60 FPS ON ken32.png:
        // While the user is actively dragging/resizing an ROI (_editor.IsPreviewing == true),
        // only publish the lightweight canvas scene without recomputing histograms, filters, or blobs!
        if (_editor.IsPreviewing)
        {
            _wasPreviewingRoiTransform = true;
            PublishCanvasScene();
            return;
        }

        RebuildCanvasRoiOptionsList();
        if (_wasPreviewingRoiTransform || _editor.State.Selection.Count > 0)
        {
            _wasPreviewingRoiTransform = false;
            UpdateFilterPreviewAndCheckCharts();
        }

        PublishCanvasScene();
    }

    private void RebuildCanvasRoiOptionsList()
    {
        if (_activeWidth <= 0 || _activeHeight <= 0)
        {
            CanvasRoiOptions.Clear();
            return;
        }

        Guid? activeRoiId = _editor.State.Selection.Count > 0
            ? _editor.State.Selection.First()
            : null;

        _suppressEvents = true;
        try
        {
            CanvasRoiOptions.Clear();
            var fullOption = new CanvasRoiOptionItem(
                Guid.Empty,
                "(전체 영상 Whole Image)",
                $"0, 0, {_activeWidth:N0}×{_activeHeight:N0}");
            CanvasRoiOptions.Add(fullOption);

            CanvasRoiOptionItem selected = fullOption;
            foreach (Roi roi in _editor.State.Rois.Values.OrderBy(r => r.Order))
            {
                BoundsD b = roi.Geometry.Bounds;
                var item = new CanvasRoiOptionItem(
                    roi.Id,
                    roi.Name,
                    $"X:{b.Left:F0}, Y:{b.Top:F0}, W:{b.Width:F0}, H:{b.Height:F0}");
                CanvasRoiOptions.Add(item);

                if (activeRoiId == roi.Id)
                {
                    selected = item;
                }
            }

            _selectedRoiOption = selected;
            OnPropertyChanged(nameof(SelectedRoiOption));
        }
        finally
        {
            _suppressEvents = false;
        }
    }

    private (Guid RoiId, string RoiName, RoiGeometry Geometry, int X, int Y, int Width, int Height, bool IsWholeImage) GetActiveRoiBounds()
    {
        if (_editor.State.Selection.Count > 0)
        {
            Guid selectedId = _editor.State.Selection.First();
            if (_editor.State.Rois.TryGetValue(selectedId, out Roi? roi))
            {
                BoundsD b = roi.Geometry.Bounds;
                int rx = Math.Clamp((int)Math.Round(b.Left), 0, Math.Max(0, _activeWidth - 1));
                int ry = Math.Clamp((int)Math.Round(b.Top), 0, Math.Max(0, _activeHeight - 1));
                int rw = Math.Clamp((int)Math.Round(b.Width), 1, Math.Max(1, _activeWidth - rx));
                int rh = Math.Clamp((int)Math.Round(b.Height), 1, Math.Max(1, _activeHeight - ry));
                string name = string.IsNullOrWhiteSpace(roi.Name) ? "ROI" : roi.Name;
                return (roi.Id, name, roi.Geometry, rx, ry, rw, rh, false);
            }
        }

        int fullW = Math.Max(1, _activeWidth);
        int fullH = Math.Max(1, _activeHeight);
        var fullGeom = new RoiGeometry(new PointD(fullW * 0.5, fullH * 0.5), fullW, fullH, 0.0);
        return (Guid.Empty, "WholeImage", fullGeom, 0, 0, fullW, fullH, true);
    }

    // ==================== Category 1: 필터 툴 (Filter Tools) Implementation ====================
    private void ExecuteAutoContrastStretch()
    {
        if (_selectedStackFrame is null)
        {
            return;
        }

        var (_, _, _, rx, ry, rw, rh, _) = GetActiveRoiBounds();
        ExtractedRoiWindow win = _loader.ExtractRoiWindow(_selectedStackFrame, rx, ry, rw, rh, MaxFilterPreviewBudgetPixels);
        if (!win.IsValid)
        {
            return;
        }

        ImageJContrastSpec autoSpec = ImageJWorkbenchEngine.ComputeAutoContrastSpec(win.AsView(), 0, 0, win.Width, win.Height);

        _contrastDisplayMin = autoSpec.DisplayMin;
        _contrastDisplayMax = autoSpec.DisplayMax;
        _contrastGamma = autoSpec.Gamma;
        _isLiveContrastPreviewEnabled = true;

        OnPropertyChanged(nameof(ContrastDisplayMin));
        OnPropertyChanged(nameof(ContrastDisplayMax));
        OnPropertyChanged(nameof(ContrastGamma));
        OnPropertyChanged(nameof(IsLiveContrastPreviewEnabled));

        UpdateFilterPreviewAndCheckCharts();
        PublishCanvasScene();
        CanvasStatusText = $"[필터 툴] Auto B&C 완료: Display Range [{autoSpec.DisplayMin}..{autoSpec.DisplayMax}], Gamma {autoSpec.Gamma:F2}.";
    }

    private void ResetContrastParameters()
    {
        _contrastDisplayMin = byte.MinValue;
        _contrastDisplayMax = byte.MaxValue;
        _contrastGamma = ImageJContrastSpec.NeutralGamma;
        _isLiveContrastPreviewEnabled = false;

        OnPropertyChanged(nameof(ContrastDisplayMin));
        OnPropertyChanged(nameof(ContrastDisplayMax));
        OnPropertyChanged(nameof(ContrastGamma));
        OnPropertyChanged(nameof(IsLiveContrastPreviewEnabled));

        UpdateFilterPreviewAndCheckCharts();
        PublishCanvasScene();
    }

    private void CommitContrastLutToPixels()
    {
        if (_selectedStackFrame is null)
        {
            return;
        }

        if (_selectedStackFrame.IsDiskTiled)
        {
            _isLiveContrastPreviewEnabled = true;
            OnPropertyChanged(nameof(IsLiveContrastPreviewEnabled));
            UpdateFilterPreviewAndCheckCharts();
            PublishCanvasScene();
            CanvasStatusText = "대용량 디스크 스트리밍 영상에서는 선택 ROI 위에 비파괴 실시간 필터 오버레이(FilterPreview)로 즉시 적용됩니다.";
            return;
        }

        var (_, roiName, _, rx, ry, rw, rh, _) = GetActiveRoiBounds();
        var srcView = new ReadOnlyImageView2D<byte>(_selectedStackFrame.WorkingPixels, _activeWidth, _activeHeight, _activeStride);
        var nextPixels = new byte[_selectedStackFrame.WorkingPixels.Length];
        _selectedStackFrame.WorkingPixels.AsSpan().CopyTo(nextPixels);

        var spec = ImageJContrastSpec.Create((byte)_contrastDisplayMin, (byte)_contrastDisplayMax, _contrastGamma);
        ImageJWorkbenchEngine.ApplyBrightnessContrastToRoi(srcView, nextPixels, _activeStride, rx, ry, rw, rh, spec);
        _selectedStackFrame.CommitPixelModification(nextPixels);

        ResetContrastParameters();
        RefreshInMemoryCanvasDisplay();
        CommandManager.InvalidateRequerySuggested();
        CanvasStatusText = $"[필터 툴] Brightness/Contrast (Min:{spec.DisplayMin}, Max:{spec.DisplayMax}, γ:{spec.Gamma:F2})를 [{roiName}]에 적용했습니다.";
    }

    private void SelectAndApplyAutoThreshold(ImageJAutoThresholdMethod method, byte computedThreshold)
    {
        _selectedAutoThresholdMethod = method;
        OnPropertyChanged(nameof(SelectedAutoThresholdMethod));
        SetThresholdBandFromAutoValue(computedThreshold);
        CanvasStatusText = $"[필터 툴] Auto-Threshold [{method} = {computedThreshold} GV]가 설정되었습니다.";
    }

    private void ApplySelectedAutoThresholdMethod()
    {
        if (_selectedAutoThresholdMethod == ImageJAutoThresholdMethod.Manual)
        {
            return;
        }

        byte target = _selectedAutoThresholdMethod switch
        {
            ImageJAutoThresholdMethod.Otsu => _lastOtsuThreshold,
            ImageJAutoThresholdMethod.Triangle => _lastTriangleThreshold,
            ImageJAutoThresholdMethod.IsoData => _lastIsoDataThreshold,
            ImageJAutoThresholdMethod.MaxEntropy => _lastMaxEntropyThreshold,
            ImageJAutoThresholdMethod.Mean => _lastMeanThreshold,
            ImageJAutoThresholdMethod.Percentile => _lastMedianThreshold,
            _ => _lastOtsuThreshold
        };

        SetThresholdBandFromAutoValue(target);
    }

    private void SetThresholdBandFromAutoValue(byte threshold)
    {
        if (_isDarkBackground)
        {
            _thresholdMin = threshold;
            _thresholdMax = byte.MaxValue;
        }
        else
        {
            _thresholdMin = byte.MinValue;
            _thresholdMax = threshold;
        }

        OnPropertyChanged(nameof(ThresholdMin));
        OnPropertyChanged(nameof(ThresholdMax));

        UpdateFilterPreviewAndCheckCharts();
        PublishCanvasScene();
    }

    private void CommitThresholdMaskToPixels()
    {
        if (_selectedStackFrame is null)
        {
            return;
        }

        if (_selectedStackFrame.IsDiskTiled)
        {
            _isLiveThresholdPreviewEnabled = true;
            OnPropertyChanged(nameof(IsLiveThresholdPreviewEnabled));
            UpdateFilterPreviewAndCheckCharts();
            PublishCanvasScene();
            CanvasStatusText = $"대용량 디스크 영상에서는 선택 ROI 위에 이진 마스크 [{_thresholdMin}..{_thresholdMax}] 오버레이가 표시됩니다.";
            return;
        }

        var (_, roiName, _, rx, ry, rw, rh, _) = GetActiveRoiBounds();
        var srcView = new ReadOnlyImageView2D<byte>(_selectedStackFrame.WorkingPixels, _activeWidth, _activeHeight, _activeStride);
        var nextPixels = new byte[_selectedStackFrame.WorkingPixels.Length];
        _selectedStackFrame.WorkingPixels.AsSpan().CopyTo(nextPixels);

        ImageJWorkbenchEngine.ApplyThresholdMaskToRoi(
            srcView,
            nextPixels,
            _activeStride,
            rx,
            ry,
            rw,
            rh,
            (byte)_thresholdMin,
            (byte)_thresholdMax);

        _selectedStackFrame.CommitPixelModification(nextPixels);

        _isLiveThresholdPreviewEnabled = false;
        OnPropertyChanged(nameof(IsLiveThresholdPreviewEnabled));
        RefreshInMemoryCanvasDisplay();
        CommandManager.InvalidateRequerySuggested();
        CanvasStatusText = $"[필터 툴] 이진화 마스크 [{_thresholdMin}..{_thresholdMax}]를 [{roiName}] 픽셀에 변환했습니다.";
    }

    private void SelectAndPreviewFilter(ImageJFilterKind kind)
    {
        for (int i = 0; i < FilterOptions.Count; i++)
        {
            if (FilterOptions[i].FilterKind == kind)
            {
                _selectedFilterOption = FilterOptions[i];
                OnPropertyChanged(nameof(SelectedFilterOption));
                break;
            }
        }

        _isLiveFilterPreviewEnabled = true;
        OnPropertyChanged(nameof(IsLiveFilterPreviewEnabled));
        UpdateFilterPreviewAndCheckCharts();
        PublishCanvasScene();
        CanvasStatusText = $"[필터 툴] [{_selectedFilterOption.DisplayName}] ROI 실시간 미리보기 활성화.";
    }

    private void ApplySpecificFilterToPixels(ImageJFilterKind kind, bool wholeImage)
    {
        if (_selectedStackFrame is null)
        {
            return;
        }

        if (_selectedStackFrame.IsDiskTiled)
        {
            SelectAndPreviewFilter(kind);
            return;
        }

        var (_, roiName, _, rx, ry, rw, rh, _) = wholeImage
            ? (Guid.Empty, "WholeImage", default, 0, 0, _activeWidth, _activeHeight, true)
            : GetActiveRoiBounds();

        var srcView = new ReadOnlyImageView2D<byte>(_selectedStackFrame.WorkingPixels, _activeWidth, _activeHeight, _activeStride);
        var nextPixels = new byte[_selectedStackFrame.WorkingPixels.Length];
        _selectedStackFrame.WorkingPixels.AsSpan().CopyTo(nextPixels);

        ImageJWorkbenchEngine.ApplyFilterToRoi(srcView, nextPixels, _activeStride, rx, ry, rw, rh, kind);
        _selectedStackFrame.CommitPixelModification(nextPixels);

        _isLiveFilterPreviewEnabled = false;
        OnPropertyChanged(nameof(IsLiveFilterPreviewEnabled));
        RefreshInMemoryCanvasDisplay();
        CommandManager.InvalidateRequerySuggested();
        CanvasStatusText = $"[필터 툴] [{kind}] 필터를 [{roiName} ({rw}×{rh})] 픽셀에 적용했습니다 (Undo 가능).";
    }

    private void ApplyFillToActiveRoi()
    {
        if (_selectedStackFrame is null || _selectedStackFrame.IsDiskTiled)
        {
            CanvasStatusText = "메모리 슬라이스에서만 직접 픽셀 채우기(Fill)가 지원됩니다.";
            return;
        }

        var (_, roiName, _, rx, ry, rw, rh, _) = GetActiveRoiBounds();
        var nextPixels = new byte[_selectedStackFrame.WorkingPixels.Length];
        _selectedStackFrame.WorkingPixels.AsSpan().CopyTo(nextPixels);

        ImageJWorkbenchEngine.FillRoi(nextPixels, _activeWidth, _activeHeight, _activeStride, rx, ry, rw, rh, (byte)_fillGrayValue);
        _selectedStackFrame.CommitPixelModification(nextPixels);

        RefreshInMemoryCanvasDisplay();
        CommandManager.InvalidateRequerySuggested();
        CanvasStatusText = $"Edit > Fill: [{roiName}] 영역을 {_fillGrayValue} GV로 채웠습니다.";
    }

    private void ApplyClearOutsideActiveRoi()
    {
        if (_selectedStackFrame is null || _selectedStackFrame.IsDiskTiled)
        {
            return;
        }

        var (_, roiName, _, rx, ry, rw, rh, isWhole) = GetActiveRoiBounds();
        if (isWhole)
        {
            CanvasStatusText = "Clear Outside를 실행하려면 먼저 부분 ROI를 선택하세요.";
            return;
        }

        var nextPixels = new byte[_selectedStackFrame.WorkingPixels.Length];
        _selectedStackFrame.WorkingPixels.AsSpan().CopyTo(nextPixels);

        ImageJWorkbenchEngine.ClearOutsideRoi(nextPixels, _activeWidth, _activeHeight, _activeStride, rx, ry, rw, rh, byte.MinValue);
        _selectedStackFrame.CommitPixelModification(nextPixels);

        RefreshInMemoryCanvasDisplay();
        CommandManager.InvalidateRequerySuggested();
        CanvasStatusText = $"Edit > Clear Outside: [{roiName}] 바깥 배경을 0 GV로 지웠습니다.";
    }

    private void RefreshInMemoryCanvasDisplay()
    {
        if (_selectedStackFrame is null || _selectedStackFrame.IsDiskTiled)
        {
            return;
        }

        _loader.UpdateActiveBuffer(_selectedStackFrame.WorkingPixels, _activeWidth, _activeHeight, _activeStride);
        UpdateFilterPreviewAndCheckCharts();

        var imageRef = new ImageReference(
            $"{ToolBlockDynamicImageLoader.LiveKeyPrefix}{_liveFrameCounter++}",
            _selectedStackFrame.FileName,
            _activeWidth,
            _activeHeight);
        List<Roi> rois = _editor.State.Rois.Values.OrderBy(r => r.Order).ToList();
        _ = _images.LoadDocumentAsync(imageRef, rois);
    }

    // ==================== Bounded ROI Window Extraction + FilterPreview + Check Charts ====================
    private ExtractedRoiWindow GetProcessedActiveRoiWindow(int rx, int ry, int rw, int rh, int pixelBudget)
    {
        ExtractedRoiWindow rawWin = _loader.ExtractRoiWindow(_selectedStackFrame, rx, ry, rw, rh, pixelBudget);
        if (!rawWin.IsValid)
        {
            return default;
        }

        bool applySpatial = _isLiveFilterPreviewEnabled && _selectedFilterOption.FilterKind != ImageJFilterKind.None;
        bool applyContrast = _isLiveContrastPreviewEnabled;
        bool applyThreshold = _isLiveThresholdPreviewEnabled;

        if (!applySpatial && !applyContrast && !applyThreshold)
        {
            return rawWin;
        }

        int byteCount = rawWin.Height * rawWin.Stride;
        var working = new byte[byteCount];
        rawWin.Pixels.AsSpan(0, byteCount).CopyTo(working);

        if (applySpatial)
        {
            var srcCopy = new byte[byteCount];
            working.AsSpan().CopyTo(srcCopy);
            var srcView = new ReadOnlyImageView2D<byte>(srcCopy, rawWin.Width, rawWin.Height, rawWin.Stride);
            ImageJWorkbenchEngine.ApplyFilterToRoi(
                srcView,
                working,
                rawWin.Stride,
                0,
                0,
                rawWin.Width,
                rawWin.Height,
                _selectedFilterOption.FilterKind);
        }

        if (applyContrast)
        {
            var srcCopy = new byte[byteCount];
            working.AsSpan().CopyTo(srcCopy);
            var srcView = new ReadOnlyImageView2D<byte>(srcCopy, rawWin.Width, rawWin.Height, rawWin.Stride);
            var contrastSpec = ImageJContrastSpec.Create((byte)_contrastDisplayMin, (byte)_contrastDisplayMax, _contrastGamma);
            ImageJWorkbenchEngine.ApplyBrightnessContrastToRoi(
                srcView,
                working,
                rawWin.Stride,
                0,
                0,
                rawWin.Width,
                rawWin.Height,
                contrastSpec);
        }

        if (applyThreshold)
        {
            var srcCopy = new byte[byteCount];
            working.AsSpan().CopyTo(srcCopy);
            var srcView = new ReadOnlyImageView2D<byte>(srcCopy, rawWin.Width, rawWin.Height, rawWin.Stride);
            ImageJWorkbenchEngine.ApplyThresholdMaskToRoi(
                srcView,
                working,
                rawWin.Stride,
                0,
                0,
                rawWin.Width,
                rawWin.Height,
                (byte)_thresholdMin,
                (byte)_thresholdMax);
        }

        return new ExtractedRoiWindow(
            working,
            rawWin.Width,
            rawWin.Height,
            rawWin.Stride,
            rawWin.WorldX,
            rawWin.WorldY,
            rawWin.WorldWidth,
            rawWin.WorldHeight,
            rawWin.ScaleFactor);
    }

    private void UpdateFilterPreviewAndCheckCharts()
    {
        if (_selectedStackFrame is null || _activeWidth <= 0 || _activeHeight <= 0)
        {
            _activeFilterPreview = null;
            return;
        }

        var (roiId, roiName, roiGeom, rx, ry, rw, rh, _) = GetActiveRoiBounds();
        ActiveRoiSummaryText = $"{roiName} (X:{rx:N0}, Y:{ry:N0}, W:{rw:N0}, H:{rh:N0} · {(long)rw * rh:N0} px)";

        // Build non-destructive ROI FilterPreviewResult for VisionCanvas when any live preview is checked
        bool anyPreviewActive =
            (_isLiveFilterPreviewEnabled && _selectedFilterOption.FilterKind != ImageJFilterKind.None) ||
            _isLiveContrastPreviewEnabled ||
            _isLiveThresholdPreviewEnabled;

        if (anyPreviewActive)
        {
            long t0 = Stopwatch.GetTimestamp();
            ExtractedRoiWindow previewWin = GetProcessedActiveRoiWindow(rx, ry, rw, rh, MaxFilterPreviewBudgetPixels);
            if (previewWin.IsValid)
            {
                // Pack tightly (stride == width) for FilteredRoi
                var tightGray = new byte[previewWin.Width * previewWin.Height];
                for (int y = 0; y < previewWin.Height; y++)
                {
                    previewWin.Pixels.AsSpan(y * previewWin.Stride, previewWin.Width)
                        .CopyTo(tightGray.AsSpan(y * previewWin.Width, previewWin.Width));
                }

                double ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                var filteredRoi = new FilteredRoi(
                    roiId == Guid.Empty ? Guid.NewGuid() : roiId,
                     new RoiGeometry(new PointD(rx + (rw * 0.5), ry + (rh * 0.5)), rw, rh, roiGeom.Angle),
                    previewWin.Width,
                    previewWin.Height,
                    tightGray);
                _activeFilterPreview = new FilterPreviewResult([filteredRoi], ms);
            }
            else
            {
                _activeFilterPreview = null;
            }
        }
        else
        {
            _activeFilterPreview = null;
        }

        RefreshHistogramAndProfileCharts();
    }

    // ==================== Category 2: 확인 툴 (Check / Verify Tools) Implementation ====================
    private void RefreshHistogramAndProfileCharts()
    {
        if (_selectedStackFrame is null || _activeWidth <= 0 || _activeHeight <= 0)
        {
            return;
        }

        var (_, _, _, rx, ry, rw, rh, _) = GetActiveRoiBounds();

        // Analyze Histogram & 1D Plot Profile on the bounded ROI window (with spatial/contrast applied, before binary mask)
        ExtractedRoiWindow rawWin = _loader.ExtractRoiWindow(_selectedStackFrame, rx, ry, rw, rh, ToolBlockDynamicImageLoader.DefaultMaxWindowPixels);
        if (!rawWin.IsValid)
        {
            return;
        }

        int byteCount = rawWin.Height * rawWin.Stride;
        byte[] targetPixels = rawWin.Pixels;

        if ((_isLiveFilterPreviewEnabled && _selectedFilterOption.FilterKind != ImageJFilterKind.None) || _isLiveContrastPreviewEnabled)
        {
            targetPixels = new byte[byteCount];
            rawWin.Pixels.AsSpan(0, byteCount).CopyTo(targetPixels);

            if (_isLiveFilterPreviewEnabled && _selectedFilterOption.FilterKind != ImageJFilterKind.None)
            {
                var srcCopy = new byte[byteCount];
                targetPixels.AsSpan().CopyTo(srcCopy);
                var srcView = new ReadOnlyImageView2D<byte>(srcCopy, rawWin.Width, rawWin.Height, rawWin.Stride);
                ImageJWorkbenchEngine.ApplyFilterToRoi(srcView, targetPixels, rawWin.Stride, 0, 0, rawWin.Width, rawWin.Height, _selectedFilterOption.FilterKind);
            }

            if (_isLiveContrastPreviewEnabled)
            {
                var srcCopy = new byte[byteCount];
                targetPixels.AsSpan().CopyTo(srcCopy);
                var srcView = new ReadOnlyImageView2D<byte>(srcCopy, rawWin.Width, rawWin.Height, rawWin.Stride);
                var contrastSpec = ImageJContrastSpec.Create((byte)_contrastDisplayMin, (byte)_contrastDisplayMax, _contrastGamma);
                ImageJWorkbenchEngine.ApplyBrightnessContrastToRoi(srcView, targetPixels, rawWin.Stride, 0, 0, rawWin.Width, rawWin.Height, contrastSpec);
            }
        }

        var view = new ReadOnlyImageView2D<byte>(targetPixels, rawWin.Width, rawWin.Height, rawWin.Stride);
        RoiHistogramAnalysis hist = RoiHistogramAnalyzer.AnalyzeRect(view, 0, 0, rawWin.Width, rawWin.Height, (byte)_thresholdMin, (byte)_thresholdMax);

        _lastOtsuThreshold = hist.OtsuThreshold;
        _lastTriangleThreshold = hist.TriangleThreshold;
        _lastIsoDataThreshold = hist.IsoDataThreshold;
        _lastMaxEntropyThreshold = hist.MaxEntropyThreshold;
        _lastMeanThreshold = hist.MeanThreshold;
        _lastMedianThreshold = hist.MedianGray;

        string scaleNote = rawWin.ScaleFactor > 1 ? $" (Pyramid 1:{rawWin.ScaleFactor} 표본)" : string.Empty;
        HistogramStatsLine1 =
            $"Samples: {hist.TotalPixelCount:N0} px{scaleNote} · Mean: {hist.MeanGray:F2} ± {hist.StdDevGray:F2} · Min/Max: {hist.MinGray} / {hist.MaxGray} · Median(P50): {hist.MedianGray} · Mode: {hist.ModeGray}";
        HistogramStatsLine2 =
            $"P5..P95: [{hist.Percentile5Gray}..{hist.Percentile95Gray}] · Skewness: {hist.Skewness:F2} · Kurtosis: {hist.Kurtosis:F2} · Entropy: {hist.EntropyBits:F2} bits";
        AutoThresholdSummaryText =
            $"Otsu: {hist.OtsuThreshold} | Triangle: {hist.TriangleThreshold} | IsoData: {hist.IsoDataThreshold} | MaxEntropy: {hist.MaxEntropyThreshold} | Mean: {hist.MeanThreshold} | P50: {hist.MedianGray}";

        PopulateHistogramBars(hist);
        PopulateProfileBars(hist, rx, ry, rw, rh);
    }

    private void PopulateHistogramBars(RoiHistogramAnalysis hist)
    {
        Span<int> groupedCounts = stackalloc int[DisplayHistogramBarCount];
        int maxBarCount = 1;
        for (int b = 0; b < DisplayHistogramBarCount; b++)
        {
            int startGv = b * BinsPerDisplayBar;
            int sum = 0;
            for (int k = 0; k < BinsPerDisplayBar; k++)
            {
                sum += hist.Bins256[startGv + k];
            }

            groupedCounts[b] = sum;
            if (sum > maxBarCount)
            {
                maxBarCount = sum;
            }
        }

        HistogramBars.Clear();
        for (int b = 0; b < DisplayHistogramBarCount; b++)
        {
            int startGv = b * BinsPerDisplayBar;
            int endGv = startGv + BinsPerDisplayBar - 1;
            int count = groupedCounts[b];
            double heightPx = count > 0
                ? Math.Max(MinVisibleBarHeightPx, (count / (double)maxBarCount) * MaxHistogramChartHeightPx)
                : 0.0;

            bool inThresholdBand = endGv >= _thresholdMin && startGv <= _thresholdMax;
            string colorHex = inThresholdBand ? "#F97316" : "#38BDF8";

            HistogramBars.Add(new HistogramBarDisplayItem(
                b,
                $"{startGv}..{endGv}",
                count,
                heightPx,
                colorHex,
                $" 밝기 [{startGv}..{endGv}] : {count:N0} px "));
        }
    }

    private void PopulateProfileBars(RoiHistogramAnalysis hist, int rx, int ry, int rw, int rh)
    {
        float[] profile = _isVerticalProfileMode ? hist.VerticalProfile : hist.HorizontalProfile;
        float[] gradient = _isVerticalProfileMode ? hist.VerticalGradientProfile : hist.HorizontalGradientProfile;

        float maxIntensity = 1.0f;
        float maxGrad = 1.0f;
        int len = Math.Min(DisplayProfileSampleCount, Math.Min(profile.Length, gradient.Length));
        for (int i = 0; i < len; i++)
        {
            if (profile[i] > maxIntensity)
            {
                maxIntensity = profile[i];
            }

            if (gradient[i] > maxGrad)
            {
                maxGrad = gradient[i];
            }
        }

        ProfileBars.Clear();
        for (int i = 0; i < len; i++)
        {
            double intHeight = Math.Max(MinVisibleBarHeightPx, (profile[i] / byte.MaxValue) * MaxHistogramChartHeightPx);
            double gradHeight = gradient[i] > 0.2f
                ? Math.Max(MinVisibleBarHeightPx, (gradient[i] / Math.Max(10.0f, maxGrad)) * (MaxHistogramChartHeightPx * 0.85))
                : 0.0;

            string axisLabel = _isVerticalProfileMode
                ? $"Y={ry + ((long)i * rh / Math.Max(1, len))}"
                : $"X={rx + ((long)i * rw / Math.Max(1, len))}";

            ProfileBars.Add(new ProfileBarDisplayItem(
                i,
                axisLabel,
                profile[i],
                gradient[i],
                intHeight,
                gradHeight,
                "#34D399",
                "#F43F5E",
                $"{axisLabel} · 평균 밝기: {profile[i]:F1} GV · 1차 미분 |∇I|: {gradient[i]:F1}"));
        }

        string dirName = _isVerticalProfileMode ? "세로 Y-단면" : "가로 X-단면";
        ProfileSummaryText =
            $"1D Plot Profile ({dirName}) · Peak 밝기: {maxIntensity:F1} GV · 최대 에지 경사 |∇I|: {maxGrad:F1}";
    }

    /// <summary>
    /// On-Demand Blob / Particle Verification in Category 2 (확인 툴).
    /// Runs only when the user clicks <c>[🔍 선택 ROI 블랍/입자 확인]</c> and caps overlay markers at 256.
    /// </summary>
    private void ExecuteAnalyzeParticlesOnDemand()
    {
        if (_selectedStackFrame is null || _activeWidth <= 0 || _activeHeight <= 0)
        {
            CanvasStatusText = "먼저 이미지를 열어주세요.";
            return;
        }

        long t0 = Stopwatch.GetTimestamp();
        var (_, roiName, _, rx, ry, rw, rh, _) = GetActiveRoiBounds();
        ExtractedRoiWindow win = _loader.ExtractRoiWindow(_selectedStackFrame, rx, ry, rw, rh, MaxInspectionWindowPixels);
        if (!win.IsValid)
        {
            return;
        }

        int byteCount = win.Height * win.Stride;
        byte[] work = win.Pixels;
        if (_isLiveFilterPreviewEnabled && _selectedFilterOption.FilterKind != ImageJFilterKind.None)
        {
            work = new byte[byteCount];
            var srcView = new ReadOnlyImageView2D<byte>(win.Pixels, win.Width, win.Height, win.Stride);
            ImageJWorkbenchEngine.ApplyFilterToRoi(srcView, work, win.Stride, 0, 0, win.Width, win.Height, _selectedFilterOption.FilterKind);
        }

        int scale = win.ScaleFactor;
        int scaleSq = scale * scale;
        int scaledMinArea = Math.Max(1, _particleMinArea / scaleSq);
        int scaledMaxArea = Math.Max(scaledMinArea, _particleMaxArea / scaleSq);

        var threshSpec = ImageJThresholdSpec.Create(
            _selectedAutoThresholdMethod,
            (byte)_thresholdMin,
            (byte)_thresholdMax,
            _isDarkBackground);

        var filterSpec = ImageJParticleFilterSpec.Create(
            scaledMinArea,
            scaledMaxArea,
            _particleMinCircularity,
            _particleMaxCircularity,
            _excludeParticlesOnEdges);

        var view = new ReadOnlyImageView2D<byte>(work, win.Width, win.Height, win.Stride);
        IReadOnlyList<ImageJParticleRecord> localParticles = _engine.AnalyzeParticles(
            view,
            0,
            0,
            win.Width,
            win.Height,
            threshSpec,
            filterSpec);

        ParticleRows.Clear();
        long totalParticleArea = 0;
        int markerCap = Math.Min(localParticles.Count, MaxOverlayParticleMarkers);
        var markers = ImmutableArray.CreateBuilder<MatchMarker>(markerCap);

        for (int i = 0; i < localParticles.Count; i++)
        {
            ImageJParticleRecord lp = localParticles[i];
            int worldArea = lp.AreaPixels * scaleSq;
            float worldCx = rx + (lp.CentroidX * scale);
            float worldCy = ry + (lp.CentroidY * scale);
            int worldBx = rx + (lp.BoundX * scale);
            int worldBy = ry + (lp.BoundY * scale);
            int worldBw = lp.BoundWidth * scale;
            int worldBh = lp.BoundHeight * scale;

            var worldRecord = new ImageJParticleRecord(
                lp.ParticleNumber,
                worldArea,
                lp.MeanGray,
                lp.MinGray,
                lp.MaxGray,
                worldCx,
                worldCy,
                worldBx,
                worldBy,
                worldBw,
                worldBh,
                lp.Circularity,
                lp.AspectRatio,
                lp.Eccentricity,
                lp.Solidity,
                lp.OrientationDeg);

            ParticleRows.Add(worldRecord);
            totalParticleArea += worldArea;

            if (i < markerCap)
            {
                ImmutableArray<PointD> box = BuildAxisAlignedBoxPoints(worldBx, worldBy, worldBw, worldBh);
                var center = new PointD(worldCx, worldCy);
                markers.Add(new MatchMarker(
                    Bounds: box,
                    ReferencePoint: center,
                    AngleRadians: lp.OrientationDeg * (Math.PI / 180.0),
                    Label: $"#{lp.ParticleNumber} ({worldArea}px²)",
                    Color: OverlayPassColor,
                    Features: [center])
                {
                    BoundsColor = OverlayPassColor,
                    BoundsThickness = 1.6
                });
            }
        }

        double elapsedMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        ImmutableArray<PointD> roiBoundary = BuildAxisAlignedBoxPoints(rx, ry, rw, rh);
        _particlesOverlay = markers.Count > 0
            ? new ResultOverlay(
                Summary: $"[확인 툴 · 블랍/입자 확인] 검출 {localParticles.Count}개 (표시 상한 {markers.Count}개 · {elapsedMs:F1} ms)",
                Color: OverlayRoiColor,
                SearchBoundary: roiBoundary,
                Matches: markers.ToImmutable(),
                ShowFeatures: true)
            : null;
        _inspectionOverlay = null;
        _showParticlesOverlay = true;
        OnPropertyChanged(nameof(ShowParticlesOverlay));

        double areaPct = ((double)rw * rh) > 0 ? (totalParticleArea * 100.0) / ((double)rw * rh) : 0.0;
        ParticleSummaryText =
            $"블랍/입자 확인 완료: [{roiName}] 검출 {localParticles.Count:N0}개 (임계값 [{_thresholdMin}..{_thresholdMax}], %Area {areaPct:F2}%, {elapsedMs:F1} ms)";
        SelectedBottomTabIndex = 2;
        PublishCanvasScene();
        CanvasStatusText = ParticleSummaryText;
    }

    private void ClearParticlesOverlay()
    {
        _particlesOverlay = null;
        ParticleRows.Clear();
        ParticleSummaryText = "블랍/입자 오버레이 및 목록을 초기화했습니다.";
        PublishCanvasScene();
    }

    private void MeasureActiveRoiIntoTable()
    {
        if (_selectedStackFrame is null || _activeWidth <= 0 || _activeHeight <= 0)
        {
            return;
        }

        var (_, roiName, _, rx, ry, rw, rh, _) = GetActiveRoiBounds();
        ExtractedRoiWindow win = GetProcessedActiveRoiWindow(rx, ry, rw, rh, ToolBlockDynamicImageLoader.DefaultMaxWindowPixels);
        if (!win.IsValid)
        {
            return;
        }

        var threshSpec = ImageJThresholdSpec.Create(_selectedAutoThresholdMethod, (byte)_thresholdMin, (byte)_thresholdMax, _isDarkBackground);
        ImageJMeasurementRecord localRec = _engine.MeasureRoi(
            win.AsView(),
            0,
            0,
            win.Width,
            win.Height,
            _measurementSequence++,
            $"{_selectedStackFrame.FileName}:{roiName}",
            threshSpec);

        int scale = win.ScaleFactor;
        long worldArea = (long)rw * rh;
        var worldRec = localRec with
        {
            RoiX = rx,
            RoiY = ry,
            RoiWidth = rw,
            RoiHeight = rh,
            AreaPixels = (int)Math.Min(int.MaxValue, worldArea),
            CentroidX = rx + (localRec.CentroidX * scale),
            CentroidY = ry + (localRec.CentroidY * scale),
            CenterOfMassX = rx + (localRec.CenterOfMassX * scale),
            CenterOfMassY = ry + (localRec.CenterOfMassY * scale),
            IntegratedDensity = localRec.MeanGray * worldArea
        };

        MeasuredRows.Add(worldRec);
        ResultsSummaryText = $"ROI 계측 기록: 총 {MeasuredRows.Count}건 · 최근 [{roiName}] Mean={worldRec.MeanGray:F2}, Area={worldRec.AreaPixels:N0} px";
        SelectedBottomTabIndex = 3;
        CanvasStatusText = $"[확인 툴 · ROI 계측] [{roiName}] 측정 결과를 기록표 (#{worldRec.MeasurementNumber})에 추가했습니다.";
    }

    private void MultiMeasureAllRoisIntoTable()
    {
        if (_selectedStackFrame is null || _activeWidth <= 0 || _activeHeight <= 0)
        {
            return;
        }

        var threshSpec = ImageJThresholdSpec.Create(_selectedAutoThresholdMethod, (byte)_thresholdMin, (byte)_thresholdMax, _isDarkBackground);
        int addedCount = 0;

        foreach (Roi roi in _editor.State.Rois.Values.OrderBy(r => r.Order))
        {
            BoundsD b = roi.Geometry.Bounds;
            int rx = Math.Clamp((int)Math.Round(b.Left), 0, Math.Max(0, _activeWidth - 1));
            int ry = Math.Clamp((int)Math.Round(b.Top), 0, Math.Max(0, _activeHeight - 1));
            int rw = Math.Clamp((int)Math.Round(b.Width), 1, Math.Max(1, _activeWidth - rx));
            int rh = Math.Clamp((int)Math.Round(b.Height), 1, Math.Max(1, _activeHeight - ry));

            ExtractedRoiWindow win = GetProcessedActiveRoiWindow(rx, ry, rw, rh, ToolBlockDynamicImageLoader.DefaultMaxWindowPixels);
            if (!win.IsValid)
            {
                continue;
            }

            ImageJMeasurementRecord localRec = _engine.MeasureRoi(
                win.AsView(),
                0,
                0,
                win.Width,
                win.Height,
                _measurementSequence++,
                $"{_selectedStackFrame.FileName}:{roi.Name}",
                threshSpec);

            int scale = win.ScaleFactor;
            long worldArea = (long)rw * rh;
            MeasuredRows.Add(localRec with
            {
                RoiX = rx,
                RoiY = ry,
                RoiWidth = rw,
                RoiHeight = rh,
                AreaPixels = (int)Math.Min(int.MaxValue, worldArea),
                CentroidX = rx + (localRec.CentroidX * scale),
                CentroidY = ry + (localRec.CentroidY * scale),
                CenterOfMassX = rx + (localRec.CenterOfMassX * scale),
                CenterOfMassY = ry + (localRec.CenterOfMassY * scale),
                IntegratedDensity = localRec.MeanGray * worldArea
            });
            addedCount++;
        }

        if (addedCount == 0)
        {
            MeasureActiveRoiIntoTable();
            return;
        }

        ResultsSummaryText = $"ROI 계측 기록: 총 {MeasuredRows.Count}건 (Multi-Measure로 {addedCount}개 ROI 일괄 추가)";
        SelectedBottomTabIndex = 3;
        CanvasStatusText = $"[확인 툴] {addedCount}개 ROI를 일괄 측정하여 기록표에 추가했습니다.";
    }

    private void ClearMeasurementsTable()
    {
        MeasuredRows.Clear();
        _measurementSequence = 1;
        ResultsSummaryText = "ROI 계측 기록 0건 (Ctrl+M으로 현재 ROI 통계 추가)";
    }

    private void ExportMeasurementsToCsvDialog()
    {
        if (MeasuredRows.Count == 0)
        {
            CanvasStatusText = "내보낼 측정 기록이 없습니다. 먼저 [Measure (Ctrl+M)]를 실행하세요.";
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "ROI 측정 기록표 CSV 내보내기",
            FileName = "ROI_Measurements.csv",
            Filter = "CSV File (*.csv)|*.csv"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var sb = new StringBuilder(2048);
        sb.AppendLine("No,Label,X,Y,Width,Height,Area,Mean,StdDev,Min,Max,Median,Mode,IntDen,CentroidX,CentroidY,CenterOfMassX,CenterOfMassY,Skewness,Kurtosis,EntropyBits,Otsu,AreaFractionPercent");
        foreach (ImageJMeasurementRecord r in MeasuredRows)
        {
            sb.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{r.MeasurementNumber},\"{r.Label}\",{r.RoiX},{r.RoiY},{r.RoiWidth},{r.RoiHeight},{r.AreaPixels},{r.MeanGray:F3},{r.StdDevGray:F3},{r.MinGray},{r.MaxGray},{r.MedianGray},{r.ModeGray},{r.IntegratedDensity:F1},{r.CentroidX:F2},{r.CentroidY:F2},{r.CenterOfMassX:F2},{r.CenterOfMassY:F2},{r.Skewness:F3},{r.Kurtosis:F3},{r.EntropyBits:F3},{r.OtsuThreshold},{r.ThresholdedAreaPercent:F2}"));
        }

        File.WriteAllText(dialog.FileName, sb.ToString(), Encoding.UTF8);
        CanvasStatusText = $"ROI 계측 기록 ({MeasuredRows.Count}행)을 CSV로 저장했습니다: {dialog.FileName}";
    }

    // ==================== Category 3: 검사 툴 (Inspection Tools) Implementation ====================
    private void AddSelectedPaletteToolToPipeline()
    {
        if (_selectedStackFrame is null || _activeWidth <= 0 || _activeHeight <= 0)
        {
            CanvasStatusText = "검사 툴을 추가하려면 먼저 [📂 이미지 열기 (Ctrl+O)]로 영상을 불러오세요.";
            return;
        }

        ToolboxPaletteItem tool = _selectedPaletteTool;
        var (roiId, roiName, _, rx, ry, rw, rh, isWhole) = GetActiveRoiBounds();

        // If the user has not selected a specific ROI, create or select the first canvas ROI so the inspection tool is bound to a real ROI
        if (isWhole && _editor.State.Rois.Count == 0)
        {
            EnsureDefaultWorkingRoi();
            RebuildCanvasRoiOptionsList();
            (roiId, roiName, _, rx, ry, rw, rh, isWhole) = GetActiveRoiBounds();
        }
        else if (isWhole && _editor.State.Rois.Count > 0)
        {
            Roi firstRoi = _editor.State.Rois.Values.OrderBy(r => r.Order).First();
            _editor.Select([firstRoi.Id]);
            RebuildCanvasRoiOptionsList();
            (roiId, roiName, _, rx, ry, rw, rh, isWhole) = GetActiveRoiBounds();
        }

        int nodeId = _nextInspectionNodeId++;
        string nodeName = $"{tool.ShortCode}_{nodeId:D2}";

        var spec = new EditableToolNodeSpec(
            NodeId: nodeId,
            Name: nodeName,
            Kind: tool.Kind,
            BoundRoiId: roiId == Guid.Empty ? null : roiId,
            BoundRoiName: roiName,
            PreFilter: MapImageJFilterToRoiPreFilter(_isLiveFilterPreviewEnabled ? _selectedFilterOption.FilterKind : ImageJFilterKind.None),
            AutoThreshold: _selectedAutoThresholdMethod,
            InvertThresholdPolarity: !_isDarkBackground,
            BlobMinThreshold: (byte)_thresholdMin,
            BlobMaxThreshold: (byte)_thresholdMax,
            BinaryMinThreshold: (byte)_thresholdMin,
            BinaryMaxThreshold: (byte)_thresholdMax,
            CaliperNominalRadiusPx: MathF.Max(8.0f, Math.Min(rw, rh) * 0.25f));

        // Automatically wire fixture pose if there is an upstream PatternMatch or CornerAlign node
        if (spec.RequiresCanvasRoi && spec.Kind is not (VisionToolKind.ShapePatternMatch or VisionToolKind.DualRakeCornerAlign))
        {
            EditableToolNodeSpec? fixtureNode = _inspectionSpecs.LastOrDefault(
                s => s.Kind is VisionToolKind.ShapePatternMatch or VisionToolKind.DualRakeCornerAlign);
            if (fixtureNode is not null)
            {
                string outPort = fixtureNode.Kind == VisionToolKind.ShapePatternMatch
                    ? PatMaxToolNode.PortOutPose
                    : CornerAlignmentToolNode.PortOutPose;
                _inspectionLinks.Add(new EditablePortLink(
                    fixtureNode.NodeId,
                    outPort,
                    nodeId,
                    "InFixture",
                    TerminalDataKind.Pose));
            }
        }

        _inspectionSpecs.Add(spec);
        RebuildInspectionNodeDisplayList(selectNodeId: nodeId);
        SelectedBottomTabIndex = 4;
        CanvasStatusText = $"[검사 툴] [{nodeName} ({tool.DisplayName})]을 [{roiName}]에 바인딩하여 추가했습니다. [▶ 검사 실행]을 눌러 판정을 확인하세요.";
    }

    private void BindActiveCanvasRoiToSelectedNode()
    {
        if (_selectedInspectionNode is null)
        {
            return;
        }

        var (roiId, roiName, _, _, _, _, _, isWhole) = GetActiveRoiBounds();
        if (isWhole || roiId == Guid.Empty)
        {
            CanvasStatusText = "캔버스에서 바인딩할 개별 ROI를 먼저 선택하세요.";
            return;
        }

        int idx = _inspectionSpecs.FindIndex(s => s.NodeId == _selectedInspectionNode.NodeId);
        if (idx < 0)
        {
            return;
        }

        _inspectionSpecs[idx] = _inspectionSpecs[idx] with
        {
            BoundRoiId = roiId,
            BoundRoiName = roiName
        };

        RebuildInspectionNodeDisplayList(selectNodeId: _selectedInspectionNode.NodeId);
        CanvasStatusText = $"[{_inspectionSpecs[idx].Name}] 검사 노드의 대상 영역을 [{roiName}]로 변경했습니다.";
    }

    private void CopyActiveThresholdToSelectedNode()
    {
        if (_selectedInspectionNode is null)
        {
            return;
        }

        _selectedNodeBlobMinThreshold = _thresholdMin;
        _selectedNodeBlobMaxThreshold = _thresholdMax;
        _selectedNodeBinaryMinThreshold = _thresholdMin;
        _selectedNodeBinaryMaxThreshold = _thresholdMax;
        _selectedNodeAutoThreshold = _selectedAutoThresholdMethod;
        _selectedNodePreFilter = MapImageJFilterToRoiPreFilter(_isLiveFilterPreviewEnabled ? _selectedFilterOption.FilterKind : ImageJFilterKind.None);

        OnPropertyChanged(nameof(SelectedNodeBlobMinThreshold));
        OnPropertyChanged(nameof(SelectedNodeBlobMaxThreshold));
        OnPropertyChanged(nameof(SelectedNodeBinaryMinThreshold));
        OnPropertyChanged(nameof(SelectedNodeBinaryMaxThreshold));
        OnPropertyChanged(nameof(SelectedNodeAutoThreshold));
        OnPropertyChanged(nameof(SelectedNodePreFilter));

        UpdateSelectedNodeSpecInList();
        CanvasStatusText = $"[필터 툴]의 현재 임계값 [{_thresholdMin}..{_thresholdMax}] 및 전처리 설정을 [{_selectedNodeName}] 노드에 복사했습니다.";
    }

    private void RemoveSelectedInspectionNode()
    {
        if (_selectedInspectionNode is null)
        {
            return;
        }

        int removeId = _selectedInspectionNode.NodeId;
        _inspectionSpecs.RemoveAll(s => s.NodeId == removeId);
        _inspectionLinks.RemoveAll(l => l.SourceNodeId == removeId || l.TargetNodeId == removeId);
        RebuildInspectionNodeDisplayList(selectNodeId: null);
        CanvasStatusText = $"검사 노드 #{removeId}를 삭제했습니다.";
    }

    private void ClearAllInspectionNodes()
    {
        _inspectionSpecs.Clear();
        _inspectionLinks.Clear();
        InspectionNodeRows.Clear();
        InspectionResultRows.Clear();
        _inspectionOverlay = null;
        InspectionVerdictBadge = "IDLE";
        InspectionVerdictHex = "#475569";
        InspectionSummaryText = "검사 파이프라인이 초기화되었습니다.";
        PublishCanvasScene();
    }

    private void LoadSelectedNodeParametersIntoEditor()
    {
        if (_selectedInspectionNode is null)
        {
            return;
        }

        EditableToolNodeSpec? spec = _inspectionSpecs.FirstOrDefault(s => s.NodeId == _selectedInspectionNode.NodeId);
        if (spec is null)
        {
            return;
        }

        _suppressEvents = true;
        try
        {
            _selectedNodeName = spec.Name;
            _selectedNodePreFilter = spec.PreFilter;
            _selectedNodeAutoThreshold = spec.AutoThreshold;
            _selectedNodePatMaxMinScore = spec.PatMaxMinScore;
            _selectedNodePatMaxAngleDeg = spec.PatMaxAngleRangeDeg;
            _selectedNodeBlobMinThreshold = spec.BlobMinThreshold;
            _selectedNodeBlobMaxThreshold = spec.BlobMaxThreshold;
            _selectedNodeBlobMinArea = spec.BlobMinAreaPixels;
            _selectedNodeBinaryMinThreshold = spec.BinaryMinThreshold;
            _selectedNodeBinaryMaxThreshold = spec.BinaryMaxThreshold;
            _selectedNodeBinaryMinPassPct = spec.BinaryMinPassPercent;
            _selectedNodeBinaryMaxPassPct = spec.BinaryMaxPassPercent;
            _selectedNodeCaliperMode = spec.CaliperMode;
            _selectedNodeCaliperMinContrast = spec.CaliperMinContrast;
            _selectedNodeCaliperNominalRadius = spec.CaliperNominalRadiusPx;
            _selectedNodeVariationAbsThreshold = spec.VariationAbsThreshold;
            _selectedNodeVariationMinDefectArea = spec.VariationMinDefectAreaPx;
            _selectedNodeJudgeLsl = spec.JudgeLsl;
            _selectedNodeJudgeUsl = spec.JudgeUsl;

            OnPropertyChanged(nameof(SelectedNodeName));
            OnPropertyChanged(nameof(SelectedNodePreFilter));
            OnPropertyChanged(nameof(SelectedNodeAutoThreshold));
            OnPropertyChanged(nameof(SelectedNodePatMaxMinScore));
            OnPropertyChanged(nameof(SelectedNodePatMaxAngleDeg));
            OnPropertyChanged(nameof(SelectedNodeBlobMinThreshold));
            OnPropertyChanged(nameof(SelectedNodeBlobMaxThreshold));
            OnPropertyChanged(nameof(SelectedNodeBlobMinArea));
            OnPropertyChanged(nameof(SelectedNodeBinaryMinThreshold));
            OnPropertyChanged(nameof(SelectedNodeBinaryMaxThreshold));
            OnPropertyChanged(nameof(SelectedNodeBinaryMinPassPct));
            OnPropertyChanged(nameof(SelectedNodeBinaryMaxPassPct));
            OnPropertyChanged(nameof(SelectedNodeCaliperMode));
            OnPropertyChanged(nameof(SelectedNodeCaliperMinContrast));
            OnPropertyChanged(nameof(SelectedNodeCaliperNominalRadius));
            OnPropertyChanged(nameof(SelectedNodeVariationAbsThreshold));
            OnPropertyChanged(nameof(SelectedNodeVariationMinDefectArea));
            OnPropertyChanged(nameof(SelectedNodeJudgeLsl));
            OnPropertyChanged(nameof(SelectedNodeJudgeUsl));
        }
        finally
        {
            _suppressEvents = false;
        }
    }

    private void UpdateSelectedNodeSpecInList()
    {
        if (_suppressEvents || _selectedInspectionNode is null)
        {
            return;
        }

        int idx = _inspectionSpecs.FindIndex(s => s.NodeId == _selectedInspectionNode.NodeId);
        if (idx < 0)
        {
            return;
        }

        EditableToolNodeSpec current = _inspectionSpecs[idx];
        _inspectionSpecs[idx] = current with
        {
            Name = string.IsNullOrWhiteSpace(_selectedNodeName) ? current.Name : _selectedNodeName.Trim(),
            PreFilter = _selectedNodePreFilter,
            AutoThreshold = _selectedNodeAutoThreshold,
            PatMaxMinScore = _selectedNodePatMaxMinScore,
            PatMaxAngleRangeDeg = _selectedNodePatMaxAngleDeg,
            BlobMinThreshold = (byte)_selectedNodeBlobMinThreshold,
            BlobMaxThreshold = (byte)_selectedNodeBlobMaxThreshold,
            BlobMinAreaPixels = _selectedNodeBlobMinArea,
            BinaryMinThreshold = (byte)_selectedNodeBinaryMinThreshold,
            BinaryMaxThreshold = (byte)_selectedNodeBinaryMaxThreshold,
            BinaryMinPassPercent = _selectedNodeBinaryMinPassPct,
            BinaryMaxPassPercent = _selectedNodeBinaryMaxPassPct,
            CaliperMode = _selectedNodeCaliperMode,
            CaliperMinContrast = _selectedNodeCaliperMinContrast,
            CaliperNominalRadiusPx = _selectedNodeCaliperNominalRadius,
            VariationAbsThreshold = (byte)_selectedNodeVariationAbsThreshold,
            VariationMinDefectAreaPx = _selectedNodeVariationMinDefectArea,
            JudgeLsl = _selectedNodeJudgeLsl,
            JudgeUsl = _selectedNodeJudgeUsl
        };
    }

    private void RebuildInspectionNodeDisplayList(int? selectNodeId)
    {
        _suppressEvents = true;
        try
        {
            InspectionNodeRows.Clear();
            DagNodeDisplayItem? toSelect = null;
            for (int i = 0; i < _inspectionSpecs.Count; i++)
            {
                EditableToolNodeSpec s = _inspectionSpecs[i];
                var item = new DagNodeDisplayItem(
                    ExecutionStep: i + 1,
                    NodeId: s.NodeId,
                    Name: s.Name,
                    Kind: s.Kind,
                    ToolTypeBadge: DynamicToolBlockRuntime.FormatToolShortBadge(s.Kind),
                    BoundRoiId: s.BoundRoiId,
                    BoundRoiLabel: s.BoundRoiName,
                    InPortSummary: $"Pre:{s.PreFilter}",
                    OutPortSummary: $"Thr:{s.AutoThreshold}",
                    LiveMetricSummary: "Ready (클릭: [▶ 검사 실행])",
                    StatusBadge: "READY",
                    StatusHex: "#38BDF8");
                InspectionNodeRows.Add(item);
                if (selectNodeId == s.NodeId)
                {
                    toSelect = item;
                }
            }

            _selectedInspectionNode = toSelect ?? InspectionNodeRows.LastOrDefault();
            OnPropertyChanged(nameof(SelectedInspectionNode));
        }
        finally
        {
            _suppressEvents = false;
        }

        LoadSelectedNodeParametersIntoEditor();
    }

    /// <summary>
    /// Executes the user-configured ToolBlock Inspection Pipeline.
    /// On huge disk images like <c>ken32.png</c>, extracts the bounding union window of all bound ROIs
    /// (with margin) from the pyramid tiles, runs the zero-allocation DAG hot path in local window coordinates,
    /// and maps all <see cref="ResultOverlay"/> markers back to world coordinates!
    /// </summary>
    private void ExecuteInspectionPipelineNow()
    {
        if (_selectedStackFrame is null || _activeWidth <= 0 || _activeHeight <= 0)
        {
            CanvasStatusText = "먼저 [📂 이미지 열기 (Ctrl+O)]로 검사할 영상을 불러오세요.";
            return;
        }

        if (_inspectionSpecs.Count == 0)
        {
            AddSelectedPaletteToolToPipeline();
            if (_inspectionSpecs.Count == 0)
            {
                return;
            }
        }

        // Compute bounding union of all ROIs referenced by _inspectionSpecs
        (int winX, int winY, int winW, int winH) = ComputeInspectionUnionBounds();
        ExtractedRoiWindow win = _loader.ExtractRoiWindow(_selectedStackFrame, winX, winY, winW, winH, MaxInspectionWindowPixels);
        if (!win.IsValid)
        {
            CanvasStatusText = "검사 영역 타일 추출에 실패했습니다.";
            return;
        }

        int scale = win.ScaleFactor;
        DocumentState shiftedState = BuildWindowLocalDocumentState(_editor.State, winX, winY, scale);
        ReadOnlyImageView2D<byte> view = win.AsView();

        DynamicToolBlockSnapshot snapshot = _toolBlockRuntime.CompileAndExecute(
            view,
            view,
            shiftedState,
            _inspectionSpecs,
            _inspectionLinks);

        // Map overlay markers from local window coordinates back to world image coordinates
        _inspectionOverlay = MapOverlayToWorld(snapshot.CanvasOverlay, winX, winY, scale);
        _particlesOverlay = null;

        _suppressEvents = true;
        try
        {
            int? prevSelectedId = _selectedInspectionNode?.NodeId;
            InspectionNodeRows.Clear();
            DagNodeDisplayItem? restoredSelection = null;
            foreach (DagNodeDisplayItem dagItem in snapshot.OrderedDagNodes)
            {
                InspectionNodeRows.Add(dagItem);
                if (dagItem.NodeId == prevSelectedId)
                {
                    restoredSelection = dagItem;
                }
            }

            _selectedInspectionNode = restoredSelection ?? InspectionNodeRows.FirstOrDefault();
            OnPropertyChanged(nameof(SelectedInspectionNode));
        }
        finally
        {
            _suppressEvents = false;
        }

        InspectionResultRows.Clear();
        foreach (AlgorithmResultSummaryRow row in snapshot.ResultSummaryRows)
        {
            InspectionResultRows.Add(row);
        }

        bool isOk = snapshot.VerdictResult.Decision == FinalDecision.Ok;
        InspectionVerdictBadge = isOk ? "PASS (OK)" : "FAIL (NG)";
        InspectionVerdictHex = isOk ? "#10B981" : "#EF4444";
        InspectionSummaryText =
            $"판정: {InspectionVerdictBadge} · 통과 게이트: {snapshot.VerdictResult.PassedGateCount}/{snapshot.VerdictResult.EvaluatedGateCount} · 실행 시간: {snapshot.ExecutionMicroseconds:F1} µs";

        SelectedBottomTabIndex = 4;
        PublishCanvasScene();
        CanvasStatusText = $"[검사 툴] 실행 완료: {InspectionSummaryText}";
    }

    private (int X, int Y, int Width, int Height) ComputeInspectionUnionBounds()
    {
        if (!_selectedStackFrame!.IsDiskTiled)
        {
            return (0, 0, _activeWidth, _activeHeight);
        }

        int minX = int.MaxValue;
        int minY = int.MaxValue;
        int maxX = 0;
        int maxY = 0;
        bool anyRoi = false;

        for (int i = 0; i < _inspectionSpecs.Count; i++)
        {
            Guid? rid = _inspectionSpecs[i].BoundRoiId;
            if (rid is { } id && _editor.State.Rois.TryGetValue(id, out Roi? roi))
            {
                BoundsD b = roi.Geometry.Bounds;
                minX = Math.Min(minX, (int)Math.Floor(b.Left));
                minY = Math.Min(minY, (int)Math.Floor(b.Top));
                maxX = Math.Max(maxX, (int)Math.Ceiling(b.Right));
                maxY = Math.Max(maxY, (int)Math.Ceiling(b.Bottom));
                anyRoi = true;
            }
        }

        if (!anyRoi)
        {
            var (_, _, _, rx, ry, rw, rh, _) = GetActiveRoiBounds();
            minX = rx;
            minY = ry;
            maxX = rx + rw;
            maxY = ry + rh;
        }

        const int MarginPx = 32;
        int x = Math.Clamp(minX - MarginPx, 0, Math.Max(0, _activeWidth - 1));
        int y = Math.Clamp(minY - MarginPx, 0, Math.Max(0, _activeHeight - 1));
        int right = Math.Clamp(maxX + MarginPx, x + 1, _activeWidth);
        int bottom = Math.Clamp(maxY + MarginPx, y + 1, _activeHeight);
        return (x, y, right - x, bottom - y);
    }

    private static DocumentState BuildWindowLocalDocumentState(DocumentState worldState, int winX, int winY, int scale)
    {
        if (winX == 0 && winY == 0 && scale == 1)
        {
            return worldState;
        }

        double invScale = 1.0 / Math.Max(1, scale);
        var builder = ImmutableDictionary.CreateBuilder<Guid, Roi>();
        foreach (KeyValuePair<Guid, Roi> kvp in worldState.Rois)
        {
            Roi r = kvp.Value;
            RoiGeometry g = r.Geometry;
            var localCenter = new PointD((g.Center.X - winX) * invScale, (g.Center.Y - winY) * invScale);
            var localGeom = new RoiGeometry(
                localCenter,
                Math.Max(16.0, g.Width * invScale),
                Math.Max(16.0, g.Height * invScale),
                g.Angle);
            builder[kvp.Key] = r with { Geometry = localGeom };
        }

        return new DocumentState(
            builder.ToImmutable(),
            worldState.Selection,
            worldState.Image,
            worldState.Viewport);
    }

    private static ResultOverlay MapOverlayToWorld(ResultOverlay localOverlay, int winX, int winY, int scale)
    {
        if (winX == 0 && winY == 0 && scale == 1)
        {
            return localOverlay;
        }

        PointD ToWorld(PointD p) => new(winX + (p.X * scale), winY + (p.Y * scale));

        ImmutableArray<PointD> MapPoly(ImmutableArray<PointD> pts)
        {
            if (pts.IsDefaultOrEmpty)
            {
                return pts;
            }

            var b = ImmutableArray.CreateBuilder<PointD>(pts.Length);
            for (int i = 0; i < pts.Length; i++)
            {
                b.Add(ToWorld(pts[i]));
            }

            return b.MoveToImmutable();
        }

        var mappedMatches = ImmutableArray.CreateBuilder<MatchMarker>(localOverlay.Matches.Length);
        foreach (MatchMarker m in localOverlay.Matches)
        {
            var mappedContours = ImmutableArray.CreateBuilder<ImmutableArray<PointD>>(m.AdditionalContours.Length);
            for (int i = 0; i < m.AdditionalContours.Length; i++)
            {
                mappedContours.Add(MapPoly(m.AdditionalContours[i]));
            }

            mappedMatches.Add(m with
            {
                Bounds = MapPoly(m.Bounds),
                ReferencePoint = ToWorld(m.ReferencePoint),
                Features = MapPoly(m.Features),
                AdditionalContours = mappedContours.MoveToImmutable()
            });
        }

        return localOverlay with
        {
            SearchBoundary = MapPoly(localOverlay.SearchBoundary),
            Matches = mappedMatches.MoveToImmutable()
        };
    }

    private static RoiPreFilterKind MapImageJFilterToRoiPreFilter(ImageJFilterKind kind) => kind switch
    {
        ImageJFilterKind.GaussianBlur3x3 or ImageJFilterKind.GaussianBlur5x5 or ImageJFilterKind.MeanSmooth3x3 => RoiPreFilterKind.Gaussian3x3,
        ImageJFilterKind.Median3x3 => RoiPreFilterKind.Median3x3,
        ImageJFilterKind.SobelEdgeMagnitude or ImageJFilterKind.MorphGradient3x3 => RoiPreFilterKind.SobelGradient,
        ImageJFilterKind.UnsharpSharpen3x3 => RoiPreFilterKind.UnsharpSharpen3x3,
        ImageJFilterKind.MorphErode3x3 => RoiPreFilterKind.MorphErode3x3,
        ImageJFilterKind.MorphDilate3x3 => RoiPreFilterKind.MorphDilate3x3,
        ImageJFilterKind.MorphOpen3x3 => RoiPreFilterKind.MorphOpen3x3,
        ImageJFilterKind.MorphClose3x3 => RoiPreFilterKind.MorphClose3x3,
        ImageJFilterKind.RollingBallTopHat or ImageJFilterKind.BlackBottomHat => RoiPreFilterKind.RollingBallTopHat,
        ImageJFilterKind.EnhanceContrastStretch or ImageJFilterKind.HistogramEqualize => RoiPreFilterKind.EnhanceContrastStretch,
        ImageJFilterKind.Invert => RoiPreFilterKind.Invert,
        _ => RoiPreFilterKind.None
    };

    private static ImmutableArray<PointD> BuildAxisAlignedBoxPoints(int x, int y, int w, int h) =>
    [
        new PointD(x, y),
        new PointD(x + w, y),
        new PointD(x + w, y + h),
        new PointD(x, y + h)
    ];

    private void OnCanvasPointerMoved(PointD screenPoint)
    {
        if (_disposed || _selectedStackFrame is null || _activeWidth <= 0 || _activeHeight <= 0)
        {
            return;
        }

        PointD imagePt = _editor.State.Viewport.ScreenToImage(screenPoint);
        int ix = (int)Math.Floor(imagePt.X);
        int iy = (int)Math.Floor(imagePt.Y);

        ImageJPixelProbeResult probe = _loader.ProbePixel(_selectedStackFrame, ix, iy, _engine);
        if (!probe.IsInsideImage)
        {
            PixelProbeText = $"Cursor (X: {ix:N0}, Y: {iy:N0}) — 영상 범위 밖";
            return;
        }

        PixelProbeText =
            $"Cursor (X: {probe.X:N0}, Y: {probe.Y:N0}) | Value = {probe.GrayValue} GV | 3×3 Mean = {probe.Mean3x3:F1} [{probe.Min3x3}..{probe.Max3x3}] | Sobel |∇I| = {probe.SobelGradient:F1}";
    }

    private void PublishCanvasScene()
    {
        if (_disposed)
        {
            return;
        }

        ResultOverlay? activeOverlay = _inspectionOverlay ?? (_showParticlesOverlay ? _particlesOverlay : null);

        Scene = new RenderScene(
            Document: _editor.State,
            Image: _images.Image,
            Generation: _images.Generation,
            Width: Layout.Width,
            Height: Layout.Height,
            Dpi: Layout.Dpi,
            Overlay: _rawInput.Overlay,
            FilterPreview: _activeFilterPreview,
            Transform: _editor.TransformPreview,
            Inspection: activeOverlay);
        OnPropertyChanged(nameof(Scene));
        OnPropertyChanged(nameof(IsNavigationEnabled));
    }

    private static IReadOnlyList<ImageJFilterOptionItem> BuildFilterOptions() =>
    [
        new(ImageJFilterKind.GaussianBlur3x3, "Filters", "Gaussian Blur 3×3 (σ=0.85)", "가우시안 저역통과 노이즈 스무딩"),
        new(ImageJFilterKind.GaussianBlur5x5, "Filters", "Gaussian Blur 5×5 (σ=1.40)", "광역 가우시안 노이즈 억제"),
        new(ImageJFilterKind.Median3x3, "Filters", "Median 3×3 (비선형 중앙값)", "에지 보존 Salt & Pepper 노이즈 제거"),
        new(ImageJFilterKind.MeanSmooth3x3, "Filters", "Mean Smooth 3×3 (박스 평균)", "3×3 균일 평균 필터"),
        new(ImageJFilterKind.UnsharpSharpen3x3, "Filters", "Sharpen / Unsharp Mask 3×3", "미세 에지 선명화"),
        new(ImageJFilterKind.SobelEdgeMagnitude, "Find Edges", "Sobel Edge |∇I| (에지 강도)", "1차 미분 에지 크기 추출"),
        new(ImageJFilterKind.VarianceTexture3x3, "Filters", "Variance 3×3 (국소 표면 거칠기)", "국소 질감/스크래치 분산 강조"),
        new(ImageJFilterKind.RollingBallTopHat, "Background", "Rolling-Ball Top-Hat (밝은 배경 불균일 제거)", "조명 음영 제거 및 밝은 입자 평탄화"),
        new(ImageJFilterKind.BlackBottomHat, "Background", "Black Bottom-Hat (어두운 결함/크랙 추출)", "밝은 배경 위 어두운 스크래치 강조"),
        new(ImageJFilterKind.MorphErode3x3, "Morphology", "Erode 3×3 (침식 — 최소값)", "밝은 영역 축소 및 미세 돌기 제거"),
        new(ImageJFilterKind.MorphDilate3x3, "Morphology", "Dilate 3×3 (팽창 — 최대값)", "밝은 영역 확장 및 끊어진 선 연결"),
        new(ImageJFilterKind.MorphOpen3x3, "Morphology", "Open 3×3 (열림 = 침식 후 팽창)", "작은 밝은 노이즈 점 제거"),
        new(ImageJFilterKind.MorphClose3x3, "Morphology", "Close 3×3 (닫힘 = 팽창 후 침식)", "입자 내부 작은 구멍(Pin-hole) 메우기"),
        new(ImageJFilterKind.MorphGradient3x3, "Morphology", "Morphological Gradient 3×3", "Dilate − Erode 외곽선 추출"),
        new(ImageJFilterKind.EnhanceContrastStretch, "Contrast", "Contrast Stretch (Min..Max 0..255 정규화)", "선택 영역 동적 범위 최대 확장"),
        new(ImageJFilterKind.HistogramEqualize, "Contrast", "Histogram Equalization (CDF 평활화)", "누적 분포 함수 기반 대비 평활화"),
        new(ImageJFilterKind.Invert, "Edit", "Invert (255 − I 반전)", "밝기 반전")
    ];

    private static IReadOnlyList<ToolboxPaletteItem> BuildInspectionToolPalette() =>
    [
        new(VisionToolKind.ShapePatternMatch, "1. 형상 패턴 매칭 (ShapePatternMatch)", "PatMax", "Fixturing", "#38BDF8", "ROI", "선택 ROI의 에지 형상 템플릿 학습 및 회전/위치(X,Y,θ) 검출"),
        new(VisionToolKind.DualRakeCornerAlign, "2. 직교 코너 정합 (DualRakeCornerAlign)", "Corner2Rake", "Fixturing", "#0EA5E9", "ROI", "수평/수직 듀얼 레이크 서브픽셀 에지 피팅으로 코너 교점(X,Y,∠) 검출"),
        new(VisionToolKind.SubpixelCaliperMetrology, "3. 서브픽셀 캘리퍼 치수 (SubpixelCaliper)", "CaliperMetro", "Metrology", "#10B981", "ROI", "원형 반경(SpokeCircle), 에지 폭(EdgePair), 직선(RakeLine) 서브픽셀 치수 계측"),
        new(VisionToolKind.RleBlobAnalyzer, "4. RLE 블랍 외관 검사 (RleBlobAnalyzer)", "BlobRle", "Blob", "#34D399", "ROI", "선택 ROI 내 이진 블랍 면적·중심·주축 각도 검출 및 양불 판정"),
        new(VisionToolKind.BinaryPixelCounter, "5. 유효 픽셀 비율 판정 (BinaryPixelCounter)", "BinaryCount", "Presence", "#F59E0B", "ROI", "임계값 범위 내 유효 픽셀 개수 및 면적 비율(%Area) 유무 검사"),
        new(VisionToolKind.GoldenSurfaceInspector, "6. 골든 표면 이물/결함 검사 (GoldenSurface)", "GoldenDiff", "Surface", "#F43F5E", "ROI", "기준 영상 대비 국소 밝기 편차(Z-Score) 기반 스크래치/이물 검출"),
        new(VisionToolKind.ToleranceSpecJudge, "7. 공차 규격 판정기 (ToleranceSpecJudge)", "SpecJudge", "Judge", "#A855F7", "Link", "상류 측정치가 LSL ~ USL 공차 범위 이내인지 OK/NG 판정")
    ];

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _editor.Changed -= OnEditorChanged;
        _rawInput.Changed -= PublishCanvasScene;
        Layout.Changed -= PublishCanvasScene;
        _images.Changed -= PublishCanvasScene;
        _toolBlockRuntime.Dispose();
        _engine.Dispose();
        _images.Dispose();
        _loader.Dispose();
    }
}
