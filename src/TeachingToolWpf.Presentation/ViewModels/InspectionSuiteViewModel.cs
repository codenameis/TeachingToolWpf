using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Numerics;
using TeachingToolWpf.Commands;
using TeachingToolWpf.Imaging.Matching;
using TeachingToolWpf.Infrastructure;
using TeachingToolWpf.Models;
using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Geometry;
using VisionCanvas.Rendering;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.ViewModels;

public enum InspectionSuiteCategory
{
    All = 0,
    MetrologyAndGeometry = 1,
    SurfaceBlobAnd3D = 2,
    CodeAndOcr = 3,
    AiAndAutoTeaching = 4,
    PreprocessingAndFrequency = 5
}

public sealed record InspectionSuiteAlgorithmOption(
    InspectionSuiteAlgorithmKind Kind,
    string Name,
    InspectionSuiteCategory Category = InspectionSuiteCategory.All,
    string ShortPillLabel = "")
{
    public RelayCommand? SelectCommand { get; init; }
}
public sealed record MetrologyModeOption(MetrologyMeasurementMode Mode, string Name);
public sealed record MetrologyPolarityOption(MetrologyPolarityMode Polarity, string Name);

public sealed record SuiteRegisteredReferenceItem(
    Guid RoiId,
    string Name,
    string RoleBadge,
    string RoleAccent,
    string GeometrySummary,
    string ChildTargetSummary,
    RelayCommand RemoveCommand);

/// <summary>
/// 상용 정밀 검사 스위트 7종 통합 뷰모델.
/// 선택된 ROI 또는 문서 전체 ROI에 대해 7종 알고리즘을 실행하고 결과를 캔버스 오버레이로 시각화하며,
/// ROI 이동·회전·파라미터 조절 시 실시간 캘리퍼 가이드 및 자동 계측 프리뷰를 제공한다.
/// </summary>
public sealed class InspectionSuiteViewModel : ObservableObject, IVisionAlgorithmPreview
{
    private static readonly Vector4 PassColor = new(0.06f, 0.72f, 0.51f, 1.0f);
    private static readonly Vector4 FailColor = new(0.94f, 0.27f, 0.27f, 1.0f);
    private static readonly Vector4 GuideBoundsColor = new(0.22f, 0.74f, 0.97f, 0.88f);
    private static readonly Vector4 GuideInnerColor = new(0.38f, 0.80f, 0.98f, 0.78f);
    private static readonly Vector4 PrimaryFeatureColor = new(0.0f, 0.90f, 1.0f, 1.0f);
    private static readonly Vector4 SecondaryFeatureColor = new(0.98f, 0.80f, 0.08f, 1.0f);
    private static readonly Vector4 DefectFeatureColor = new(1.0f, 0.23f, 0.19f, 1.0f);

    private readonly IInspectionSuiteService _service;
    private readonly List<InspectionSuiteRoiInput> _referenceRois = [];
    private InspectionSuiteCategory _selectedCategory = InspectionSuiteCategory.MetrologyAndGeometry;
    private InspectionSuiteAlgorithmKind _selectedAlgorithm = InspectionSuiteAlgorithmKind.Metrology;
    private MetrologyMeasurementMode _metrologyMode = MetrologyMeasurementMode.Auto;
    private MetrologyPolarityMode _polarityMode = MetrologyPolarityMode.Any;
    private int _caliperRakeCount = 15;
    private float _caliperProjectionWidth = 11.0f;
    private float _gaussianSigma = 1.2f;
    private float _minimumContrast = 18.0f;
    private int _thresholdIntensity = 128;
    private int _morphologyRadius = 1;
    private float _variationStdMultiplier = 3.0f;
    private float _variationEdgeMultiplier = 0.5f;
    private float _nominalBeadWidth = 14.0f;
    private int _patchSize = 8;
    private float _gridPitchMm = 10.0f;
    private float _lensK1 = -0.05f;
    private int _goldenMinDefectAreaPixels = 6;
    private int _blobMinimumAreaPixels = 8;
    private float _fewShotOodSigmaMultiplier = 3.5f;
    private int _fewShotMaxSubClusters = 2;
    private float _padimRegularizationLambda = 0.01f;
    private float _cornerApexIgnoreMargin = 36.0f;
    private float _cornerRansacThresholdPixels = 0.55f;
    private float _cornerOrthogonalityToleranceDeg = 5.0f;
    private float _ocrAcceptThreshold = 0.40f;
    private float _ocrMinQualityScore = 0.75f;
    private float _ocrForegroundContrast = 14.0f;
    private int _ocrExpectedCharPitch = 24;
    private int _ocrDotClosingRadius = 1;
    private int _lightweightOcrBeamWidth = 4;
    private int _lightweightOcrColumnStride = 2;
    private float _lightweightOcrMinConfidence = 0.40f;
    private float _lightweightOcrContrastOffset = 12.0f;
    private float _deepOcrSuperResScale = 1.5f;
    private float _deepOcrUnsharpGain = 0.38f;
    private int _deepOcrEnsembleBeamWidth = 6;
    private float _deepOcrContrastOffset = 14.0f;
    private float _deepOcrMinConfidence = 0.40f;
    private float _autoTeachMinGeometricScore = 0.45f;
    private float _autoTeachMinAppearanceCosine = 0.35f;
    private float _autoTeachMaxMahalanobis = 35.0f;
    private int _shadingTargetBaseGray = 128;
    private float _anisotropicDiffusionKappa = 18.0f;
    private int _reconstructionMarkerRadius = 2;
    private float _compositeExpectedSpanPx = 28.0f;
    private int _ellipseSpokeCount = 24;
    private float _ellipseMaxRmsErrorPx = 1.8f;
    private int _watershedThresholdIntensity = 128;
    private float _watershedMinPeakDistancePx = 14.0f;
    private int _watershedMinAreaPixels = 12;
    private float _dataMatrixMinIsoContrast = 0.20f;
    private int _dataMatrixMaxEccErrors = 8;
    private float _fftNotchOrGaborSigma = 2.5f;
    private float _fftPeakThresholdRatio = 4.2f;
    private float _homomorphicGammaHigh = 1.85f;
    private int _barcodeScanlineCount = 9;
    private float _barcodeMinSymbolContrast = 0.20f;
    private float _photometricSlantAngleDeg = 45.0f;
    private float _photometricCurvatureThreshold = 0.08f;
    private int _photometricMaxDefectPixels = 6;
    private float _gdtAngleToleranceDeg = 3.0f;
    private float _gdtConcentricityLimitPx = 2.5f;
    private float _gdtDistanceTolerancePx = 4.0f;
    private float _gdtGradientThreshold = 18.0f;
    private DocumentState _state = DocumentState.Empty;
    private object? _lastRoisRef;
    private object? _lastSelectionRef;
    private RoiTransformPreview? _transformPreview;
    private ImageReference? _image;
    private long _generation;
    private long _epoch;
    private bool _live;
    private bool _canExecute;
    private bool _toolAvailable = true;
    private bool _hasInputErrors;
    private bool _showOverlay = true;
    private bool _autoPreview = true;
    private bool _disposed;
    private CancellationTokenSource? _cancellation;
    private CancellationTokenSource? _previewCancellation;
    private LivePreviewRequest? _pendingPreview;
    private Task? _previewPump;
    private long _previewEpoch;
    private long _lastPublishedPreviewEpoch;
    private ResultOverlay? _overlay;
    private ResultOverlay? _livePreview;
    private bool _isHelpManualOpen;
    private RelayCommand? _toggleHelpManualCommand;

    public bool IsHelpManualOpen
    {
        get => _isHelpManualOpen;
        set => Set(ref _isHelpManualOpen, value);
    }

    public RelayCommand ToggleHelpManual => _toggleHelpManualCommand ??= new(() => IsHelpManualOpen = !IsHelpManualOpen);

    public AlgorithmHelpManual ActiveHelpManual => AlgorithmHelpCatalog.ForInspectionSuite(_selectedAlgorithm);

    private sealed record LivePreviewRequest(
        ImageReference Image,
        bool Live,
        InspectionSuiteAlgorithmKind Algorithm,
        InspectionSuiteRoiInput[] Targets,
        InspectionSuiteRoiInput[] References,
        InspectionSuiteParameters Parameters,
        SynchronizationContext? UiContext,
        long Epoch,
        long Generation);

    public InspectionSuiteViewModel(IInspectionSuiteService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;

        InspectionSuiteAlgorithmOption CreateOption(
            InspectionSuiteAlgorithmKind kind,
            InspectionSuiteCategory category,
            string shortPillLabel)
            => new(kind, InspectionSuiteService.GetAlgorithmShortName(kind), category, shortPillLabel)
            {
                SelectCommand = new RelayCommand(() => SelectedAlgorithm = kind, () => IsIdle)
            };

        Algorithms =
        [
            CreateOption(InspectionSuiteAlgorithmKind.Metrology, InspectionSuiteCategory.MetrologyAndGeometry, "1D 캘리퍼·직선·원"),
            CreateOption(InspectionSuiteAlgorithmKind.GoldenVariation, InspectionSuiteCategory.SurfaceBlobAnd3D, "골든 마스터 변이"),
            CreateOption(InspectionSuiteAlgorithmKind.BlobRle, InspectionSuiteCategory.SurfaceBlobAnd3D, "RLE 블롭·모폴로지"),
            CreateOption(InspectionSuiteAlgorithmKind.Curvilinear, InspectionSuiteCategory.SurfaceBlobAnd3D, "Steger 곡선 비드"),
            CreateOption(InspectionSuiteAlgorithmKind.FewShot, InspectionSuiteCategory.AiAndAutoTeaching, "Few-Shot 분류"),
            CreateOption(InspectionSuiteAlgorithmKind.PatchAnomaly, InspectionSuiteCategory.SurfaceBlobAnd3D, "PaDiM 패치 이상"),
            CreateOption(InspectionSuiteAlgorithmKind.Calibration, InspectionSuiteCategory.MetrologyAndGeometry, "9-Point 캘리브레이션"),
            CreateOption(InspectionSuiteAlgorithmKind.CornerAlignment, InspectionSuiteCategory.MetrologyAndGeometry, "2-Rake 가상 코너"),
            CreateOption(InspectionSuiteAlgorithmKind.IndustrialOcrOcv, InspectionSuiteCategory.CodeAndOcr, "산업용 OCR/OCV"),
            CreateOption(InspectionSuiteAlgorithmKind.LightweightOcr, InspectionSuiteCategory.CodeAndOcr, "경량 고속 OCR"),
            CreateOption(InspectionSuiteAlgorithmKind.AutoTeaching, InspectionSuiteCategory.AiAndAutoTeaching, "멀티파츠 자동 티칭"),
            CreateOption(InspectionSuiteAlgorithmKind.CpuDeepOcr, InspectionSuiteCategory.CodeAndOcr, "CPU 딥러닝 OCR"),
            CreateOption(InspectionSuiteAlgorithmKind.PreprocessingShadingAndRecon, InspectionSuiteCategory.PreprocessingAndFrequency, "조명보정·확산·재구성"),
            CreateOption(InspectionSuiteAlgorithmKind.AdvancedMetrologyEllipse, InspectionSuiteCategory.MetrologyAndGeometry, "복합캘리퍼·타원 피팅"),
            CreateOption(InspectionSuiteAlgorithmKind.AdvancedBlobWatershed, InspectionSuiteCategory.SurfaceBlobAnd3D, "워터셰드 블롭 분리"),
            CreateOption(InspectionSuiteAlgorithmKind.DataMatrix2DReader, InspectionSuiteCategory.CodeAndOcr, "2D DataMatrix"),
            CreateOption(InspectionSuiteAlgorithmKind.PreprocessingFftAndGabor, InspectionSuiteCategory.PreprocessingAndFrequency, "2D FFT·호모모픽·Gabor"),
            CreateOption(InspectionSuiteAlgorithmKind.Barcode1DReader, InspectionSuiteCategory.CodeAndOcr, "1D 바코드 리더"),
            CreateOption(InspectionSuiteAlgorithmKind.PhotometricStereo, InspectionSuiteCategory.SurfaceBlobAnd3D, "포토메트릭 3D 표면"),
            CreateOption(InspectionSuiteAlgorithmKind.GeometricTolerancing, InspectionSuiteCategory.MetrologyAndGeometry, "다중형상 GD&T 공차")
        ];

        SelectMetrologyCategory = new RelayCommand(() => SelectCategoryAndDefaultAlgorithm(InspectionSuiteCategory.MetrologyAndGeometry), () => IsIdle);
        SelectSurface3DCategory = new RelayCommand(() => SelectCategoryAndDefaultAlgorithm(InspectionSuiteCategory.SurfaceBlobAnd3D), () => IsIdle);
        SelectCodeOcrCategory = new RelayCommand(() => SelectCategoryAndDefaultAlgorithm(InspectionSuiteCategory.CodeAndOcr), () => IsIdle);
        SelectAiAutoTeachingCategory = new RelayCommand(() => SelectCategoryAndDefaultAlgorithm(InspectionSuiteCategory.AiAndAutoTeaching), () => IsIdle);
        SelectPreprocessingCategory = new RelayCommand(() => SelectCategoryAndDefaultAlgorithm(InspectionSuiteCategory.PreprocessingAndFrequency), () => IsIdle);
        SelectAllCategories = new RelayCommand(() => SelectedCategory = InspectionSuiteCategory.All, () => IsIdle);

        MetrologyModes =
        [
            new(MetrologyMeasurementMode.Auto, "자동 판별 (ROI 형상 기준: 폭 + 직선/원)"),
            new(MetrologyMeasurementMode.EdgePairWidth, "1D 양측 에지 폭 계측 (트레이스·슬롯·비드 폭)"),
            new(MetrologyMeasurementMode.RakeLineFit, "다중 캘리퍼 레이크 직선 피팅 (경계선 각도·진직도)"),
            new(MetrologyMeasurementMode.SpokeCircleFit, "방사형 스포크 원 피팅 (홀·원형 패드 반지름·진원도)")
        ];

        PolarityModes =
        [
            new(MetrologyPolarityMode.Any, "모든 에지 극성 (|g| 최대 대비)"),
            new(MetrologyPolarityMode.BrightStripe, "밝은 띠/트레이스 (좌: 어두움→밝음 / 우: 밝음→어두움)"),
            new(MetrologyPolarityMode.DarkSlot, "어두운 홈/슬롯 (좌: 밝음→어두움 / 우: 어두움→밝음)"),
            new(MetrologyPolarityMode.DarkToLight, "어두움 → 밝음 (상승 에지 +g 전용)"),
            new(MetrologyPolarityMode.LightToDark, "밝음 → 어두움 (하강 에지 -g 전용)")
        ];

        RegisterNormalReference = new(() => RegisterSelectedRois(classLabel: 0),
            () => IsIdle && _canExecute && _image is not null && HasSelectedLeafRoi());
        RegisterDefectReference = new(() => RegisterSelectedRois(classLabel: 1),
            () => IsIdle && _canExecute && _image is not null && HasSelectedLeafRoi());
        ClearReferences = new(ClearAllReferences,
            () => IsIdle && (_referenceRois.Count > 0 || _overlay is not null));
        ExecuteSelected = new(() => ExecuteRoisAsync(selectedOnly: true), error => PresentWarning(error.Message),
            () => IsIdle && _canExecute && _image is not null && (HasSelectedLeafRoi() || HasAnyLeafRoi()) && OptionsError.Length == 0);
        ExecuteAllRois = new(() => ExecuteRoisAsync(selectedOnly: false), error => PresentWarning(error.Message),
            () => IsIdle && _canExecute && _image is not null && HasAnyLeafRoi() && OptionsError.Length == 0);
        RunMaster8kBenchmark = new(RunMaster8kSuiteInternalAsync, error => PresentWarning(error.Message),
            () => IsIdle && _canExecute);
        LoadSelectedAlgorithmSample = new(() => RequestSampleLoad(_selectedAlgorithm), () => IsIdle);
        LoadMetrologySample = new(() => RequestSampleLoad(InspectionSuiteAlgorithmKind.Metrology), () => IsIdle);
        LoadGoldenVariationSample = new(() => RequestSampleLoad(InspectionSuiteAlgorithmKind.GoldenVariation), () => IsIdle);
        LoadBlobRleSample = new(() => RequestSampleLoad(InspectionSuiteAlgorithmKind.BlobRle), () => IsIdle);
        LoadCurvilinearSample = new(() => RequestSampleLoad(InspectionSuiteAlgorithmKind.Curvilinear), () => IsIdle);
        LoadFewShotSample = new(() => RequestSampleLoad(InspectionSuiteAlgorithmKind.FewShot), () => IsIdle);
        LoadPatchAnomalySample = new(() => RequestSampleLoad(InspectionSuiteAlgorithmKind.PatchAnomaly), () => IsIdle);
        LoadCalibrationSample = new(() => RequestSampleLoad(InspectionSuiteAlgorithmKind.Calibration), () => IsIdle);
        LoadCornerAlignmentSample = new(() => RequestSampleLoad(InspectionSuiteAlgorithmKind.CornerAlignment), () => IsIdle);
        LoadIndustrialOcrOcvSample = new(() => RequestSampleLoad(InspectionSuiteAlgorithmKind.IndustrialOcrOcv), () => IsIdle);
        LoadLightweightOcrSample = new(() => RequestSampleLoad(InspectionSuiteAlgorithmKind.LightweightOcr), () => IsIdle);
        LoadAutoTeachingSample = new(() => RequestSampleLoad(InspectionSuiteAlgorithmKind.AutoTeaching), () => IsIdle);
        Cancel = new(() => _cancellation?.Cancel(), () => IsBusy);
        Locate = new(() => LocateRequested?.Invoke(BoundsD.FromPoints((Overlay ?? _overlay)!.Matches.SelectMany(marker => marker.Bounds))),
            () => IsIdle && _canExecute && (Overlay ?? _overlay)?.Matches.IsEmpty == false);
    }

    public event Action? Changed;
    public event Action<BoundsD>? LocateRequested;
    public event Action<InspectionSuiteAlgorithmKind>? SampleLoadRequested;
    public event Action<RoiAlgorithmAssignment>? RoiAssignmentRecorded;
    public event Action<StructuredInspectionLogEntry>? StructuredLogRecorded;

    public IReadOnlyList<InspectionSuiteAlgorithmOption> Algorithms { get; }
    public IReadOnlyList<InspectionSuiteAlgorithmOption> CategoryAlgorithms => _selectedCategory == InspectionSuiteCategory.All
        ? Algorithms
        : Algorithms.Where(a => a.Category == _selectedCategory).ToList();

    public InspectionSuiteCategory SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (Set(ref _selectedCategory, value))
            {
                NotifyCategoryFlags();
            }
        }
    }

    public bool IsMetrologyCategorySelected => _selectedCategory == InspectionSuiteCategory.MetrologyAndGeometry;
    public bool IsSurface3DCategorySelected => _selectedCategory == InspectionSuiteCategory.SurfaceBlobAnd3D;
    public bool IsCodeOcrCategorySelected => _selectedCategory == InspectionSuiteCategory.CodeAndOcr;
    public bool IsAiAutoTeachingCategorySelected => _selectedCategory == InspectionSuiteCategory.AiAndAutoTeaching;
    public bool IsPreprocessingCategorySelected => _selectedCategory == InspectionSuiteCategory.PreprocessingAndFrequency;
    public bool IsAllCategoriesSelected => _selectedCategory == InspectionSuiteCategory.All;

    public string ActiveCategoryTitle => _selectedCategory switch
    {
        InspectionSuiteCategory.MetrologyAndGeometry => "치수·기하계측 카테고리 (5종: 1D 캘리퍼·직선·원 / 복합캘리퍼·타원 / 2-Rake 코너 / 다중형상 GD&T / 9-Point 캘리브레이션)",
        InspectionSuiteCategory.SurfaceBlobAnd3D => "블롭·표면·3D 카테고리 (6종: RLE 블롭 / 워터셰드 분리 / Steger 곡선비드 / 골든 마스터 / PaDiM 패치 / 포토메트릭 3D)",
        InspectionSuiteCategory.CodeAndOcr => "바코드·OCR 카테고리 (5종: 1D 바코드 / 2D DataMatrix / 산업용 OCR·OCV / 경량 고속 OCR / CPU 딥러닝 OCR)",
        InspectionSuiteCategory.AiAndAutoTeaching => "AI·자동티칭 카테고리 (2종: 멀티파츠 자동 티칭 / Few-Shot 이상 분류)",
        InspectionSuiteCategory.PreprocessingAndFrequency => "조명·주파수 전처리 카테고리 (2종: 2D 다항식 조명·비등방성 확산·재구성 / 2D FFT 노치·호모모픽·Gabor)",
        _ => "전체 검사 알고리즘 보기 (20종 전체 목록)"
    };

    public RelayCommand SelectMetrologyCategory { get; }
    public RelayCommand SelectSurface3DCategory { get; }
    public RelayCommand SelectCodeOcrCategory { get; }
    public RelayCommand SelectAiAutoTeachingCategory { get; }
    public RelayCommand SelectPreprocessingCategory { get; }
    public RelayCommand SelectAllCategories { get; }
    public IReadOnlyList<MetrologyModeOption> MetrologyModes { get; }
    public IReadOnlyList<MetrologyPolarityOption> PolarityModes { get; }
    public ObservableCollection<SuiteRegisteredReferenceItem> RegisteredReferenceItems { get; } = [];
    public RelayCommand RegisterNormalReference { get; }
    public RelayCommand RegisterDefectReference { get; }
    public RelayCommand ClearReferences { get; }
    public AsyncCommand ExecuteSelected { get; }
    public AsyncCommand ExecuteAllRois { get; }
    public AsyncCommand RunMaster8kBenchmark { get; }
    public RelayCommand LoadSelectedAlgorithmSample { get; }
    public RelayCommand LoadMetrologySample { get; }
    public RelayCommand LoadGoldenVariationSample { get; }
    public RelayCommand LoadBlobRleSample { get; }
    public RelayCommand LoadCurvilinearSample { get; }
    public RelayCommand LoadFewShotSample { get; }
    public RelayCommand LoadPatchAnomalySample { get; }
    public RelayCommand LoadCalibrationSample { get; }
    public RelayCommand LoadCornerAlignmentSample { get; }
    public RelayCommand LoadIndustrialOcrOcvSample { get; }
    public RelayCommand LoadLightweightOcrSample { get; }
    public RelayCommand LoadAutoTeachingSample { get; }
    public RelayCommand Cancel { get; }
    public RelayCommand Locate { get; }

    public bool IsBusy => _cancellation is not null;
    public bool IsIdle => !_disposed && !IsBusy;
    public ResultOverlay? Overlay => ShowOverlay ? (_overlay ?? _livePreview) : null;
    public InspectionSuiteExecutionOutput? LastOutput { get; private set; }

    public bool IsMetrologySelected => _selectedAlgorithm == InspectionSuiteAlgorithmKind.Metrology;
    public bool IsGoldenVariationSelected => _selectedAlgorithm == InspectionSuiteAlgorithmKind.GoldenVariation;
    public bool IsBlobRleSelected => _selectedAlgorithm == InspectionSuiteAlgorithmKind.BlobRle;
    public bool IsCurvilinearSelected => _selectedAlgorithm == InspectionSuiteAlgorithmKind.Curvilinear;
    public bool IsFewShotSelected => _selectedAlgorithm == InspectionSuiteAlgorithmKind.FewShot;
    public bool IsPatchAnomalySelected => _selectedAlgorithm == InspectionSuiteAlgorithmKind.PatchAnomaly;
    public bool IsCalibrationSelected => _selectedAlgorithm == InspectionSuiteAlgorithmKind.Calibration;
    public bool IsCornerAlignmentSelected => _selectedAlgorithm == InspectionSuiteAlgorithmKind.CornerAlignment;
    public bool IsIndustrialOcrOcvSelected => _selectedAlgorithm == InspectionSuiteAlgorithmKind.IndustrialOcrOcv;
    public bool IsLightweightOcrSelected => _selectedAlgorithm == InspectionSuiteAlgorithmKind.LightweightOcr;
    public bool IsAutoTeachingSelected => _selectedAlgorithm == InspectionSuiteAlgorithmKind.AutoTeaching;
    public bool IsCpuDeepOcrSelected => _selectedAlgorithm == InspectionSuiteAlgorithmKind.CpuDeepOcr;
    public bool IsPreprocessingShadingSelected => _selectedAlgorithm == InspectionSuiteAlgorithmKind.PreprocessingShadingAndRecon;
    public bool IsAdvancedMetrologyEllipseSelected => _selectedAlgorithm == InspectionSuiteAlgorithmKind.AdvancedMetrologyEllipse;
    public bool IsAdvancedBlobWatershedSelected => _selectedAlgorithm == InspectionSuiteAlgorithmKind.AdvancedBlobWatershed;
    public bool IsDataMatrix2DSelected => _selectedAlgorithm == InspectionSuiteAlgorithmKind.DataMatrix2DReader;
    public bool IsPreprocessingFftGaborSelected => _selectedAlgorithm == InspectionSuiteAlgorithmKind.PreprocessingFftAndGabor;
    public bool IsBarcode1DSelected => _selectedAlgorithm == InspectionSuiteAlgorithmKind.Barcode1DReader;
    public bool IsPhotometricStereoSelected => _selectedAlgorithm == InspectionSuiteAlgorithmKind.PhotometricStereo;
    public bool IsGeometricTolerancingSelected => _selectedAlgorithm == InspectionSuiteAlgorithmKind.GeometricTolerancing;
    public bool UsesMetrologyOrCornerParams => _selectedAlgorithm is InspectionSuiteAlgorithmKind.Metrology
        or InspectionSuiteAlgorithmKind.CornerAlignment;
    public bool UsesReferenceRois => _selectedAlgorithm is InspectionSuiteAlgorithmKind.GoldenVariation
        or InspectionSuiteAlgorithmKind.FewShot
        or InspectionSuiteAlgorithmKind.PatchAnomaly
        or InspectionSuiteAlgorithmKind.AutoTeaching;
    public bool HasRegisteredReferences => RegisteredReferenceItems.Count > 0;
    public string ReferenceCardTitle => _selectedAlgorithm == InspectionSuiteAlgorithmKind.AutoTeaching
        ? "3 · 멀티파츠 모델 풀 학습(티칭) & 등록 파츠 관리"
        : "기준(정상/불량) ROI 샘플 등록 & 관리";
    public string RegisterNormalButtonText => _selectedAlgorithm == InspectionSuiteAlgorithmKind.AutoTeaching
        ? "＋ 선택 ROI 파츠 학습 등록"
        : "정상(Good) 등록";
    public bool UsesEdgeContrastParams => _selectedAlgorithm is InspectionSuiteAlgorithmKind.Metrology
        or InspectionSuiteAlgorithmKind.Curvilinear
        or InspectionSuiteAlgorithmKind.CornerAlignment;

    public string Status { get; private set; } = "검사할 알고리즘을 선택하고 '현재 알고리즘 샘플 열기' 또는 '선택 ROI 검사'를 실행하세요.";
    public string ReferenceSummary { get; private set; } = "등록된 기준 ROI 없음 (미등록 시 첫 번째 대상 ROI를 기준으로 자동 학습)";
    public string Results { get; private set; } = "실행 결과 없음";

    public InspectionSuiteAlgorithmKind SelectedAlgorithm
    {
        get => _selectedAlgorithm;
        set
        {
            if (Set(ref _selectedAlgorithm, value))
            {
                var mappedCategory = ResolveCategoryForAlgorithm(value);
                if (_selectedCategory != InspectionSuiteCategory.All && _selectedCategory != mappedCategory)
                {
                    _selectedCategory = mappedCategory;
                    NotifyCategoryFlags();
                }

                _overlay = null;
                _livePreview = null;
                NotifyAlgorithmSelectionChanged();
                UpdateGeometricGuideAndSchedulePreview();
                RefreshCommands();
            }
        }
    }

    public static InspectionSuiteCategory ResolveCategoryForAlgorithm(InspectionSuiteAlgorithmKind kind) => kind switch
    {
        InspectionSuiteAlgorithmKind.Metrology
            or InspectionSuiteAlgorithmKind.AdvancedMetrologyEllipse
            or InspectionSuiteAlgorithmKind.CornerAlignment
            or InspectionSuiteAlgorithmKind.GeometricTolerancing
            or InspectionSuiteAlgorithmKind.Calibration => InspectionSuiteCategory.MetrologyAndGeometry,
        InspectionSuiteAlgorithmKind.BlobRle
            or InspectionSuiteAlgorithmKind.AdvancedBlobWatershed
            or InspectionSuiteAlgorithmKind.Curvilinear
            or InspectionSuiteAlgorithmKind.GoldenVariation
            or InspectionSuiteAlgorithmKind.PatchAnomaly
            or InspectionSuiteAlgorithmKind.PhotometricStereo => InspectionSuiteCategory.SurfaceBlobAnd3D,
        InspectionSuiteAlgorithmKind.Barcode1DReader
            or InspectionSuiteAlgorithmKind.DataMatrix2DReader
            or InspectionSuiteAlgorithmKind.IndustrialOcrOcv
            or InspectionSuiteAlgorithmKind.LightweightOcr
            or InspectionSuiteAlgorithmKind.CpuDeepOcr => InspectionSuiteCategory.CodeAndOcr,
        InspectionSuiteAlgorithmKind.AutoTeaching
            or InspectionSuiteAlgorithmKind.FewShot => InspectionSuiteCategory.AiAndAutoTeaching,
        InspectionSuiteAlgorithmKind.PreprocessingShadingAndRecon
            or InspectionSuiteAlgorithmKind.PreprocessingFftAndGabor => InspectionSuiteCategory.PreprocessingAndFrequency,
        _ => InspectionSuiteCategory.MetrologyAndGeometry
    };

    public void SelectCategoryAndDefaultAlgorithm(InspectionSuiteCategory category)
    {
        SelectedCategory = category;
        if (category == InspectionSuiteCategory.All)
        {
            return;
        }

        if (ResolveCategoryForAlgorithm(_selectedAlgorithm) != category &&
            Algorithms.FirstOrDefault(a => a.Category == category) is { } first)
        {
            SelectedAlgorithm = first.Kind;
        }
    }

    private void NotifyCategoryFlags()
    {
        Notify(nameof(SelectedCategory));
        Notify(nameof(CategoryAlgorithms));
        Notify(nameof(ActiveCategoryTitle));
        Notify(nameof(IsMetrologyCategorySelected));
        Notify(nameof(IsSurface3DCategorySelected));
        Notify(nameof(IsCodeOcrCategorySelected));
        Notify(nameof(IsAiAutoTeachingCategorySelected));
        Notify(nameof(IsPreprocessingCategorySelected));
        Notify(nameof(IsAllCategoriesSelected));
    }

    public MetrologyMeasurementMode MetrologyMode
    {
        get => _metrologyMode;
        set
        {
            if (Set(ref _metrologyMode, value))
            {
                Notify(nameof(AlgorithmUsageGuide));
                NotifyOptionValidationChanged();
            }
        }
    }

    public MetrologyPolarityMode PolarityMode
    {
        get => _polarityMode;
        set
        {
            if (Set(ref _polarityMode, value))
            {
                NotifyOptionValidationChanged();
            }
        }
    }

    public int CaliperRakeCount
    {
        get => _caliperRakeCount;
        set
        {
            int clamped = Math.Clamp(value, 5, 48);
            if (Set(ref _caliperRakeCount, clamped))
            {
                NotifyOptionValidationChanged();
            }
        }
    }

    public float CaliperProjectionWidth
    {
        get => _caliperProjectionWidth;
        set
        {
            float clamped = Math.Clamp(value, 3.0f, 45.0f);
            if (Set(ref _caliperProjectionWidth, clamped))
            {
                NotifyOptionValidationChanged();
            }
        }
    }

    public bool AutoPreview
    {
        get => _autoPreview;
        set
        {
            if (Set(ref _autoPreview, value))
            {
                if (value)
                {
                    UpdateGeometricGuideAndSchedulePreview();
                }
                else
                {
                    CancelLivePreview();
                }
            }
        }
    }

    public string AlgorithmDescription => _selectedAlgorithm switch
    {
        InspectionSuiteAlgorithmKind.Metrology =>
            "1D 가우시안 1차 미분 + 포물선 서브픽셀 에지 폭 계측, 다중 캘리퍼(Rake) RANSAC+IRLS 직선 피팅, 방사형 스포크 Taubin 원 피팅을 수행합니다.",
        InspectionSuiteAlgorithmKind.GoldenVariation =>
            "정상 ROI 샘플들의 Welford 평균(Mean)·표준편차(StdDev)와 Sobel 에지 기울기 허용치(k·σ + c·|∇μ|)를 결합해 실제 결함 픽셀만 검출합니다.",
        InspectionSuiteAlgorithmKind.BlobRle =>
            "스캔라인 런-렝스 인코딩(RLE) 압축 영역에서 구간 모폴로지(Opening)와 그린 정리(Green's Theorem) 2차 모멘트로 무게중심·주축 각도를 추출합니다.",
        InspectionSuiteAlgorithmKind.Curvilinear =>
            "Steger(1998) 2차 헤시안(Hessian) 고유값 분해로 서브픽셀 곡선 중심선을 추출하고, 경로 법선 캘리퍼로 실링 비드 단선·협소·과다를 판정합니다.",
        InspectionSuiteAlgorithmKind.FewShot =>
            "다중 구역 기울기 모멘트 + SimpleShot 정규화 + Ledoit-Wolf 수축 공분산 마할라노비스 거리로 소량 샘플 분류 및 미등록 이상치(OOD) 기각을 수행합니다.",
        InspectionSuiteAlgorithmKind.PatchAnomaly =>
            "PaDiM 방식으로 정상 ROI의 공간 패치(Grid)별 다변량 가우시안 분포를 학습하고 패치별 마할라노비스 거리 히트맵과 최대 이상치 좌표를 검출합니다.",
        InspectionSuiteAlgorithmKind.Calibration =>
            "Hartley 정규화 DLT + 9×9 야코비안 SVD 기반 9-Point 평면 호모그래피(픽셀 ↔ 물리 mm) 보정과 Brown-Conrady 렌즈 왜곡 역보정 정밀도를 검증합니다.",
        InspectionSuiteAlgorithmKind.CornerAlignment =>
            "모따기·라운딩·칩핑·버를 회피하는 2-Rake 다중 1D 서브픽셀 캘리퍼와 RANSAC + Tukey Biweight 직선 교차로 가상 코너 꼭짓점 및 4-코너 대각 정합을 수행합니다.",
        InspectionSuiteAlgorithmKind.IndustrialOcrOcv =>
            "레이저 연속 각인 및 도트 매트릭스 잉크젯 문자를 정류·분할하고, 투영 프로파일 상관·4분면 획 에너지·혼동 마진·달력 날짜 유효성까지 OCV 품질을 검증합니다.",
        InspectionSuiteAlgorithmKind.LightweightOcr =>
            "문자 개별 분할 없이 1D 열 특징 시퀀스와 CTC Prefix Beam Search로 접촉·고밀도 산업 문자열을 1-Pass로 고속 판독합니다.",
        InspectionSuiteAlgorithmKind.AutoTeaching =>
            "단일 샘플 티칭만으로 화면 내 다중 회전 부품을 자동 인식(Coarse Edge Proposal + 64B Signature + PatMax + ICGN)하고 하위 검사 ROI 월드 좌표를 즉시 생성합니다.",
        InspectionSuiteAlgorithmKind.CpuDeepOcr =>
            "GPU 없이 CPU SIMD만으로 1.5x 적응형 초해상도 언샤프닝 + 다중 대비 앙상블 + 무분할 시퀀스 빔 탐색을 결합하여 소형 비례 폰트(Arial Bold 영수증), 한글 음절, 접촉 문자열을 고속 판독합니다.",
        InspectionSuiteAlgorithmKind.PreprocessingShadingAndRecon =>
            "HALCON estimate_background_illumination / anisotropic_diffusion / opening_by_reconstruction 급 전처리: 2D 6-기저 쌍이차 다항식 조명 평탄화, Perona-Malik 비등방성 확산, Vincent(1993) 하이브리드 형태학적 재구성을 수행합니다.",
        InspectionSuiteAlgorithmKind.AdvancedMetrologyEllipse =>
            "Cognex CogCaliperScoringFunction(First/Last/Expected Span 연속 가우시안 친화도) 복합 캘리퍼와 Fitzgibbon(1999) + Sampson Gauss-Newton 직교 거리 타원(Ellipse) 피팅을 수행합니다.",
        InspectionSuiteAlgorithmKind.AdvancedBlobWatershed =>
            "RLE 스캔라인 위상 해석(Euler 내부 홀 개수·홀 면적), Monotone Chain 볼록 껍질(Convex Hull·Solidity), Rotating Calipers 최소면적 회전 외접 사각형, 명암 통계(μ,σ,min,max) 및 Meijster O(N) 유클리드 거리변환(EDT) 접촉 블롭 워터셰드 분리를 수행합니다.",
        InspectionSuiteAlgorithmKind.DataMatrix2DReader =>
            "ISO/IEC 16022 ECC 200 2D DataMatrix 리더: L-Finder 및 교번 타이밍 트랙 정합, Utah 8-bit L자 모듈 배치 복원, GF(2^8)(원시 다항식 301) Reed-Solomon 신드롬 검증·오류 정정, ISO/IEC 15415 인쇄 품질 등급(A~F)을 산출합니다.",
        InspectionSuiteAlgorithmKind.PreprocessingFftAndGabor =>
            "2D Radix-2 FFT 주기적 모아레/메쉬 스펙트럼 노치 감쇠, 호모모픽 ln(I+1) 조명·반사율 분리, 2D 직교 위상 Gabor 텍스처 강조를 수행합니다.",
        InspectionSuiteAlgorithmKind.Barcode1DReader =>
            "다중 스캔라인 서브픽셀 에지 영교차 기반 Code 128B 1D 바코드 디코딩, Modulo-103 체크섬 검증 및 ISO/IEC 15416 인쇄 품질 등급(SC/MOD/DEC Grade A~F)을 산출합니다.",
        InspectionSuiteAlgorithmKind.PhotometricStereo =>
            "Woodham 4방향 람베르시안 포토메트릭 스테레오 법선 벡터(nx,ny,nz) 및 순수 알베도(ρ) 분리, 3D 평균곡률(H)·가우시안곡률(K) 기반 미세 찍힘/스크래치 결함 검사를 수행합니다.",
        InspectionSuiteAlgorithmKind.GeometricTolerancing =>
            "다중 형상 서브픽셀 기하공차(GD&T): 수평/수직 데이텀 직선 직교도(⊥), 내/외경 이중 보어 동심도(◎ Δc), 보어-데이텀 수직거리를 정밀 계측합니다.",
        _ => string.Empty
    };

    public string AlgorithmUsageGuide => _selectedAlgorithm switch
    {
        InspectionSuiteAlgorithmKind.Metrology => _metrologyMode switch
        {
            MetrologyMeasurementMode.EdgePairWidth =>
                "① [1D 폭 계측] 캔버스에서 트레이스·슬롯 위에 ROI를 올리고, ROI 방향 화살표가 양쪽 에지를 수직으로 가로지르도록 회전하세요.\n② 하늘색 점선 박스(투영 적분 영역)와 화살표가 실시간 표시되며, 검출된 좌·우 에지(청록 점)와 서브픽셀 폭(초록 선)이 즉시 계산됩니다.",
            MetrologyMeasurementMode.RakeLineFit =>
                "① [다중 레이크 직선 피팅] 측정할 직선 경계선을 따라 ROI를 배치하고 화살표가 에지에 수직이 되도록 맞추세요.\n② 레이크 분할 수만큼 평행 스캔 보조선이 표시되며, 이상치(빨강 점)를 제외한 인라이어(청록 점)로 RANSAC+IRLS 최적 직선을 피팅합니다.",
            MetrologyMeasurementMode.SpokeCircleFit =>
                "① [방사형 스포크 원 피팅] 원형 패드·홀 중심에 정사각형 ROI를 배치하세요 (내·외측 하늘색 점선 링 사이에 원 경계가 들어오도록 크기 조절).\n② 중심에서 퍼져 나가는 스포크 보조선마다 서브픽셀 에지를 찾아 Taubin 대수적 원(반지름 R, 외경 Ø, 진원도 RMS)을 피팅합니다.",
            _ =>
                "① 상단 '현재 알고리즘 샘플 열기'를 누르거나 캔버스에서 측정할 위치에 ROI를 배치·회전하세요.\n② 계측 모드(자동 / 1D 폭 / 레이크 직선 / 스포크 원)와 시그마(σ)·최소 대비 슬라이더를 움직이면 캔버스에 스캔 가이드와 계측 결과가 실시간 프리뷰됩니다."
        },
        InspectionSuiteAlgorithmKind.GoldenVariation =>
            "① 정상(Golden) ROI들을 선택해 '정상(Good) 기준 등록'을 누르세요 (샘플 열기 시 상단 4개 다이가 자동 등록됩니다).\n② 표준편차 배수(k)와 에지 지터 허용치(c) 슬라이더를 조절하면 하단 검사 다이의 스크래치·솔더브릿지 불량 픽셀(빨강 점)이 실시간 표시됩니다.",
        InspectionSuiteAlgorithmKind.BlobRle =>
            "① 이진화 최소 밝기(Threshold)와 모폴로지 오프닝 반경(R) 슬라이더를 조절해 배경 스펙클 노이즈를 제거하세요.\n② 검출된 각 블롭의 바운딩 박스, 그린 정리 서브픽셀 무게중심(청록 점), 주축 방향선이 실시간으로 표시됩니다.",
        InspectionSuiteAlgorithmKind.Curvilinear =>
            "① 실링 비드·곡선 경로를 따라 ROI를 배치하고 비드 기준 폭(px)과 최소 에지 대비를 설정하세요.\n② Steger 헤시안 중심선(청록 점)과 단선(Gap) 발생 위치(빨강 점)가 실시간 검출됩니다.",
        InspectionSuiteAlgorithmKind.FewShot =>
            "① 정상 ROI는 '정상 기준 등록', 불량 ROI는 '불량 기준 등록'으로 등록하세요 (샘플 열기 시 자동 등록).\n② 검사 실행 시 정상(Good), 등록 불량(Defect), 미등록 이상치(OOD)를 마할라노비스 거리로 즉시 판정합니다.",
        InspectionSuiteAlgorithmKind.PatchAnomaly =>
            "① 정상 ROI들을 '정상 기준 등록'으로 등록한 뒤 패치 크기(px)를 설정하세요.\n② 타깃 ROI 내 국소 볼 누락·이물 등 최대 이상치 피크 위치(빨강/청록 점)와 마할라노비스 이상 점수를 산출합니다.",
        InspectionSuiteAlgorithmKind.Calibration =>
            "① 교정 타깃 ROI를 선택하고 실제 격자 피치(mm)와 렌즈 방사 왜곡 계수(k1)를 조절하세요.\n② 9-Point 호모그래피 물리 좌표 변환 오차(mm)와 왜곡 역보정 왕복 오차(px)가 실시간 계산됩니다.",
        InspectionSuiteAlgorithmKind.CornerAlignment =>
            "① 기판이나 부품의 코너(모따기·라운딩 포함) 부근에 ROI 중심을 배치하세요 (샘플 열기 시 4개 모따기 코너 ROI 자동 배치).\n② 코너 꼭짓점 여유 구간을 건너뛰는 2-Rake 서브픽셀 에지 스캔으로 두 직선의 정밀 가상 교차점(청록 점)과 내각(°)을 즉시 산출합니다.",
        InspectionSuiteAlgorithmKind.IndustrialOcrOcv =>
            "① 레이저 연속 각인 또는 도트 매트릭스 잉크젯 문자열 위에 수평/회전 ROI를 배치하세요.\n② 도트 브리징 정류 및 문자 분할 후, 혼동 마진과 Halcon 스타일 투영 상관 OCV 점수(≥0.80) 및 달력 날짜 유효성을 동시 검증합니다.",
        InspectionSuiteAlgorithmKind.LightweightOcr =>
            "① 웨이퍼·배치 ID 등 고밀도 문자열 라인에 ROI를 배치하세요.\n② 개별 문자 분할 없이 1D 열 특징 시퀀스와 CTC Prefix Beam Search로 빠르게 문자열과 신뢰도를 판독합니다.",
        InspectionSuiteAlgorithmKind.AutoTeaching =>
            "① 캔버스에서 학습할 부품 ROI를 선택하고 '＋ 선택 ROI 파츠 학습 등록'을 누르면 모델 풀에 64B 회전불변 서명과 하위 검사 ROI 오프셋(핀A/핀B/바디)이 즉시 티칭·등록됩니다 (샘플 열기 시 3종 파츠 자동 등록).\n② '전체 ROI 실행'을 누르면 1단계 에지 블롭 후보 추출 → 2단계 64B 서명 필터링 → 3단계 국소 PatMax + ICGN 정밀 보정으로 각 부품의 포즈와 하위 검사 ROI 월드 좌표를 자동 생성합니다.",
        InspectionSuiteAlgorithmKind.CpuDeepOcr =>
            "① 영수증(ocr2.png 좌측 소형 비례 폰트, ocr4.png), 한글 번호판(ocr5.png), 산업 마킹 위에 ROI를 그리세요.\n② 1.5x 초해상도 언샤프닝 + 다중 대비 빔 앙상블 + 수평 구분선(<hr>) 자동 소거로 저해상도·접촉 문자열과 한글 음절을 정확히 판독합니다.",
        InspectionSuiteAlgorithmKind.PreprocessingShadingAndRecon =>
            "① 불균일 조명/비네팅, 표면 그레인 노이즈, 핀홀/스패터가 있는 영역에 ROI를 배치하세요.\n② 기준 밝기(Threshold)와 마커 반경(R), 확산 대비(κ)를 조절하면 다항식 조명 평탄화·비등방성 확산·Vincent 재구성 전후 배경 편차 감소량과 분리된 객체가 즉시 표시됩니다.",
        InspectionSuiteAlgorithmKind.AdvancedMetrologyEllipse =>
            "① 다중 간섭 에지가 있는 트레이스에는 이름에 '캘리퍼'를 포함하거나, 회전된 타원 홀/패드 위에 ROI를 배치하세요.\n② 복합 스코어링(First/Last 배제 + 목표 폭 친화도) 또는 방사형 스포크 Fitzgibbon + Sampson Gauss-Newton 타원(장축 A, 단축 B, 각도 θ)이 즉시 피팅됩니다.",
        InspectionSuiteAlgorithmKind.AdvancedBlobWatershed =>
            "① 내부 홀이 있는 브래킷이나 서로 맞닿은 솔더볼/입자 군집 위에 ROI를 배치하세요 (이름에 '워터셰드' 또는 '분리' 포함 시 EDT 워터셰드 자동 활성).\n② 볼록 껍질(Convex Hull), 최소면적 회전 박스(MinAreaRect), 홀 개수·명암 통계(μ,σ)와 분리된 개별 블롭이 시각화됩니다.",
        InspectionSuiteAlgorithmKind.DataMatrix2DReader =>
            "① 2D DataMatrix(ECC 200) 심볼이 포함되도록 ROI를 그리세요.\n② L-Finder 및 타이밍 보더 검출 → Utah 모듈 추출 → GF(256) Reed-Solomon 오류 정정 → ASCII 디코딩 문자열과 ISO/IEC 15415 품질 등급(A~F)을 즉시 판독합니다.",
        InspectionSuiteAlgorithmKind.PreprocessingFftAndGabor =>
            "① 주기적 모아레 무늬, 불균일 조명(이름에 '호모모픽'), 또는 방향성 텍스처(이름에 'Gabor') 영역에 ROI를 배치하세요.\n② 주파수 노치 감쇠·조명 분리·Gabor 강조 전후 Otsu 임계값과 분리 객체가 즉시 표시됩니다.",
        InspectionSuiteAlgorithmKind.Barcode1DReader =>
            "① 1D 바코드(Code 128) 좌우 여백을 포함하도록 ROI를 그리세요.\n② 9개 다중 스캔라인 서브픽셀 에지 폭 정규화 → Modulo-103 체크섬 검증 → ISO/IEC 15416 인쇄 등급(A~F)을 즉시 출력합니다.",
        InspectionSuiteAlgorithmKind.PhotometricStereo =>
            "① 4방향 조명 패치(또는 단일 프레임 ROI)를 선택하세요.\n② 2D 얼룩(Albedo)을 완벽히 배제하고 3D 표면 법선 곡률(|H|, |K|)이 집중된 찍힘·스크래치 결함만 검출합니다.",
        InspectionSuiteAlgorithmKind.GeometricTolerancing =>
            "① 부품의 상단/좌측 데이텀 모서리와 중앙 동심 보어 홀을 감싸도록 ROI를 배치하세요.\n② 데이텀 직교각(°), 내/외경 동심도 편차(Δc px), 데이텀 수직거리 및 공차 합격 여부를 즉시 계측합니다.",
        _ => string.Empty
    };

    public float GaussianSigma
    {
        get => _gaussianSigma;
        set
        {
            if (Set(ref _gaussianSigma, value))
            {
                NotifyOptionValidationChanged();
            }
        }
    }

    public float MinimumContrast
    {
        get => _minimumContrast;
        set
        {
            if (Set(ref _minimumContrast, value))
            {
                NotifyOptionValidationChanged();
            }
        }
    }

    public int ThresholdIntensity
    {
        get => _thresholdIntensity;
        set
        {
            if (Set(ref _thresholdIntensity, value))
            {
                NotifyOptionValidationChanged();
            }
        }
    }

    public int MorphologyRadius
    {
        get => _morphologyRadius;
        set
        {
            if (Set(ref _morphologyRadius, value))
            {
                NotifyOptionValidationChanged();
            }
        }
    }

    public float VariationStdMultiplier
    {
        get => _variationStdMultiplier;
        set
        {
            if (Set(ref _variationStdMultiplier, value))
            {
                NotifyOptionValidationChanged();
            }
        }
    }

    public float VariationEdgeMultiplier
    {
        get => _variationEdgeMultiplier;
        set
        {
            if (Set(ref _variationEdgeMultiplier, value))
            {
                NotifyOptionValidationChanged();
            }
        }
    }

    public float NominalBeadWidth
    {
        get => _nominalBeadWidth;
        set
        {
            if (Set(ref _nominalBeadWidth, value))
            {
                NotifyOptionValidationChanged();
            }
        }
    }

    public int PatchSize
    {
        get => _patchSize;
        set
        {
            if (Set(ref _patchSize, value))
            {
                NotifyOptionValidationChanged();
            }
        }
    }

    public float GridPitchMm
    {
        get => _gridPitchMm;
        set
        {
            if (Set(ref _gridPitchMm, value))
            {
                NotifyOptionValidationChanged();
            }
        }
    }

    public string SelectedAlgorithmHeaderTitle => $"3 · {InspectionSuiteService.GetAlgorithmShortName(_selectedAlgorithm)} — 전용 티칭 파라미터";

    public float LensK1
    {
        get => _lensK1;
        set
        {
            if (Set(ref _lensK1, value))
            {
                NotifyOptionValidationChanged();
            }
        }
    }

    public int GoldenMinDefectAreaPixels
    {
        get => _goldenMinDefectAreaPixels;
        set
        {
            int clamped = Math.Clamp(value, 1, 500);
            if (Set(ref _goldenMinDefectAreaPixels, clamped))
            {
                NotifyOptionValidationChanged();
            }
        }
    }

    public int BlobMinimumAreaPixels
    {
        get => _blobMinimumAreaPixels;
        set
        {
            int clamped = Math.Clamp(value, 1, 5000);
            if (Set(ref _blobMinimumAreaPixels, clamped))
            {
                NotifyOptionValidationChanged();
            }
        }
    }

    public float FewShotOodSigmaMultiplier
    {
        get => _fewShotOodSigmaMultiplier;
        set
        {
            float clamped = Math.Clamp(value, 1.0f, 10.0f);
            if (Set(ref _fewShotOodSigmaMultiplier, clamped))
            {
                NotifyOptionValidationChanged();
            }
        }
    }

    public int FewShotMaxSubClusters
    {
        get => _fewShotMaxSubClusters;
        set
        {
            int clamped = Math.Clamp(value, 1, 5);
            if (Set(ref _fewShotMaxSubClusters, clamped))
            {
                NotifyOptionValidationChanged();
            }
        }
    }

    public float PadimRegularizationLambda
    {
        get => _padimRegularizationLambda;
        set
        {
            float clamped = Math.Clamp(value, 0.001f, 0.50f);
            if (Set(ref _padimRegularizationLambda, clamped))
            {
                NotifyOptionValidationChanged();
            }
        }
    }

    public float CornerApexIgnoreMargin
    {
        get => _cornerApexIgnoreMargin;
        set
        {
            float clamped = Math.Clamp(value, 16.0f, 80.0f);
            if (Set(ref _cornerApexIgnoreMargin, clamped))
            {
                NotifyOptionValidationChanged();
            }
        }
    }

    public float CornerRansacThresholdPixels
    {
        get => _cornerRansacThresholdPixels;
        set
        {
            float clamped = Math.Clamp(value, 0.10f, 5.0f);
            if (Set(ref _cornerRansacThresholdPixels, clamped))
            {
                NotifyOptionValidationChanged();
            }
        }
    }

    public float CornerOrthogonalityToleranceDeg
    {
        get => _cornerOrthogonalityToleranceDeg;
        set
        {
            float clamped = Math.Clamp(value, 0.5f, 25.0f);
            if (Set(ref _cornerOrthogonalityToleranceDeg, clamped))
            {
                NotifyOptionValidationChanged();
            }
        }
    }

    public float OcrAcceptThreshold
    {
        get => _ocrAcceptThreshold;
        set
        {
            float clamped = Math.Clamp(value, 0.10f, 0.98f);
            if (Set(ref _ocrAcceptThreshold, clamped))
            {
                NotifyOptionValidationChanged();
            }
        }
    }

    public float OcrMinQualityScore
    {
        get => _ocrMinQualityScore;
        set
        {
            float clamped = Math.Clamp(value, 0.10f, 0.98f);
            if (Set(ref _ocrMinQualityScore, clamped))
            {
                NotifyOptionValidationChanged();
            }
        }
    }

    public float OcrForegroundContrast
    {
        get => _ocrForegroundContrast;
        set
        {
            float clamped = Math.Clamp(value, 2.0f, 80.0f);
            if (Set(ref _ocrForegroundContrast, clamped))
            {
                NotifyOptionValidationChanged();
            }
        }
    }

    public int OcrExpectedCharPitch
    {
        get => _ocrExpectedCharPitch;
        set
        {
            int clamped = Math.Clamp(value, 8, 64);
            if (Set(ref _ocrExpectedCharPitch, clamped))
            {
                NotifyOptionValidationChanged();
            }
        }
    }

    public int OcrDotClosingRadius
    {
        get => _ocrDotClosingRadius;
        set
        {
            int clamped = Math.Clamp(value, 0, 5);
            if (Set(ref _ocrDotClosingRadius, clamped))
            {
                NotifyOptionValidationChanged();
            }
        }
    }

    public int LightweightOcrBeamWidth
    {
        get => _lightweightOcrBeamWidth;
        set
        {
            int clamped = Math.Clamp(value, 1, 12);
            if (Set(ref _lightweightOcrBeamWidth, clamped))
            {
                NotifyOptionValidationChanged();
            }
        }
    }

    public int LightweightOcrColumnStride
    {
        get => _lightweightOcrColumnStride;
        set
        {
            int clamped = Math.Clamp(value, 1, 6);
            if (Set(ref _lightweightOcrColumnStride, clamped))
            {
                NotifyOptionValidationChanged();
            }
        }
    }

    public float LightweightOcrMinConfidence
    {
        get => _lightweightOcrMinConfidence;
        set
        {
            float clamped = Math.Clamp(value, 0.10f, 0.98f);
            if (Set(ref _lightweightOcrMinConfidence, clamped))
            {
                NotifyOptionValidationChanged();
            }
        }
    }

    public float LightweightOcrContrastOffset
    {
        get => _lightweightOcrContrastOffset;
        set
        {
            float clamped = Math.Clamp(value, 2.0f, 80.0f);
            if (Set(ref _lightweightOcrContrastOffset, clamped))
            {
                NotifyOptionValidationChanged();
            }
        }
    }

    public float DeepOcrSuperResScale
    {
        get => _deepOcrSuperResScale;
        set
        {
            float clamped = Math.Clamp(value, 1.0f, 2.5f);
            if (Set(ref _deepOcrSuperResScale, clamped))
            {
                NotifyOptionValidationChanged();
            }
        }
    }

    public float DeepOcrUnsharpGain
    {
        get => _deepOcrUnsharpGain;
        set
        {
            float clamped = Math.Clamp(value, 0.0f, 2.0f);
            if (Set(ref _deepOcrUnsharpGain, clamped))
            {
                NotifyOptionValidationChanged();
            }
        }
    }

    public int DeepOcrEnsembleBeamWidth
    {
        get => _deepOcrEnsembleBeamWidth;
        set
        {
            int clamped = Math.Clamp(value, 1, 12);
            if (Set(ref _deepOcrEnsembleBeamWidth, clamped))
            {
                NotifyOptionValidationChanged();
            }
        }
    }

    public float DeepOcrContrastOffset
    {
        get => _deepOcrContrastOffset;
        set
        {
            float clamped = Math.Clamp(value, 2.0f, 80.0f);
            if (Set(ref _deepOcrContrastOffset, clamped))
            {
                NotifyOptionValidationChanged();
            }
        }
    }

    public float DeepOcrMinConfidence
    {
        get => _deepOcrMinConfidence;
        set
        {
            float clamped = Math.Clamp(value, 0.10f, 0.98f);
            if (Set(ref _deepOcrMinConfidence, clamped))
            {
                NotifyOptionValidationChanged();
            }
        }
    }

    public float AutoTeachMinGeometricScore
    {
        get => _autoTeachMinGeometricScore;
        set
        {
            float clamped = Math.Clamp(value, 0.10f, 0.98f);
            if (Set(ref _autoTeachMinGeometricScore, clamped))
            {
                NotifyOptionValidationChanged();
            }
        }
    }

    public float AutoTeachMinAppearanceCosine
    {
        get => _autoTeachMinAppearanceCosine;
        set
        {
            float clamped = Math.Clamp(value, 0.10f, 0.98f);
            if (Set(ref _autoTeachMinAppearanceCosine, clamped))
            {
                NotifyOptionValidationChanged();
            }
        }
    }

    public float AutoTeachMaxMahalanobis
    {
        get => _autoTeachMaxMahalanobis;
        set
        {
            float clamped = Math.Clamp(value, 3.0f, 100.0f);
            if (Set(ref _autoTeachMaxMahalanobis, clamped))
            {
                NotifyOptionValidationChanged();
            }
        }
    }

    public int ShadingTargetBaseGray
    {
        get => _shadingTargetBaseGray;
        set
        {
            int clamped = Math.Clamp(value, 16, 240);
            if (Set(ref _shadingTargetBaseGray, clamped))
            {
                NotifyOptionValidationChanged();
            }
        }
    }

    public float AnisotropicDiffusionKappa
    {
        get => _anisotropicDiffusionKappa;
        set
        {
            float clamped = Math.Clamp(value, 2.0f, 80.0f);
            if (Set(ref _anisotropicDiffusionKappa, clamped))
            {
                NotifyOptionValidationChanged();
            }
        }
    }

    public int ReconstructionMarkerRadius
    {
        get => _reconstructionMarkerRadius;
        set
        {
            int clamped = Math.Clamp(value, 1, 10);
            if (Set(ref _reconstructionMarkerRadius, clamped))
            {
                NotifyOptionValidationChanged();
            }
        }
    }

    public float CompositeExpectedSpanPx
    {
        get => _compositeExpectedSpanPx;
        set
        {
            float clamped = Math.Clamp(value, 4.0f, 160.0f);
            if (Set(ref _compositeExpectedSpanPx, clamped))
            {
                NotifyOptionValidationChanged();
            }
        }
    }

    public int EllipseSpokeCount
    {
        get => _ellipseSpokeCount;
        set
        {
            int clamped = Math.Clamp(value, 8, 64);
            if (Set(ref _ellipseSpokeCount, clamped))
            {
                NotifyOptionValidationChanged();
            }
        }
    }

    public float EllipseMaxRmsErrorPx
    {
        get => _ellipseMaxRmsErrorPx;
        set
        {
            float clamped = Math.Clamp(value, 0.2f, 8.0f);
            if (Set(ref _ellipseMaxRmsErrorPx, clamped))
            {
                NotifyOptionValidationChanged();
            }
        }
    }

    public int WatershedThresholdIntensity
    {
        get => _watershedThresholdIntensity;
        set
        {
            int clamped = Math.Clamp(value, 1, 254);
            if (Set(ref _watershedThresholdIntensity, clamped))
            {
                NotifyOptionValidationChanged();
            }
        }
    }

    public float WatershedMinPeakDistancePx
    {
        get => _watershedMinPeakDistancePx;
        set
        {
            float clamped = Math.Clamp(value, 3.0f, 80.0f);
            if (Set(ref _watershedMinPeakDistancePx, clamped))
            {
                NotifyOptionValidationChanged();
            }
        }
    }

    public int WatershedMinAreaPixels
    {
        get => _watershedMinAreaPixels;
        set
        {
            int clamped = Math.Clamp(value, 1, 5000);
            if (Set(ref _watershedMinAreaPixels, clamped))
            {
                NotifyOptionValidationChanged();
            }
        }
    }

    public float DataMatrixMinIsoContrast
    {
        get => _dataMatrixMinIsoContrast;
        set
        {
            float clamped = Math.Clamp(value, 0.05f, 0.90f);
            if (Set(ref _dataMatrixMinIsoContrast, clamped))
            {
                NotifyOptionValidationChanged();
            }
        }
    }

    public int DataMatrixMaxEccErrors
    {
        get => _dataMatrixMaxEccErrors;
        set
        {
            int clamped = Math.Clamp(value, 0, 32);
            if (Set(ref _dataMatrixMaxEccErrors, clamped))
            {
                NotifyOptionValidationChanged();
            }
        }
    }

    public float FftNotchOrGaborSigma
    {
        get => _fftNotchOrGaborSigma;
        set
        {
            float clamped = Math.Clamp(value, 0.6f, 10.0f);
            if (Set(ref _fftNotchOrGaborSigma, clamped))
            {
                NotifyOptionValidationChanged();
            }
        }
    }

    public float FftPeakThresholdRatio
    {
        get => _fftPeakThresholdRatio;
        set
        {
            float clamped = Math.Clamp(value, 1.5f, 12.0f);
            if (Set(ref _fftPeakThresholdRatio, clamped))
            {
                NotifyOptionValidationChanged();
            }
        }
    }

    public float HomomorphicGammaHigh
    {
        get => _homomorphicGammaHigh;
        set
        {
            float clamped = Math.Clamp(value, 1.0f, 3.5f);
            if (Set(ref _homomorphicGammaHigh, clamped))
            {
                NotifyOptionValidationChanged();
            }
        }
    }

    public int BarcodeScanlineCount
    {
        get => _barcodeScanlineCount;
        set
        {
            int clamped = Math.Clamp(value, 1, 25);
            if (Set(ref _barcodeScanlineCount, clamped))
            {
                NotifyOptionValidationChanged();
            }
        }
    }

    public float BarcodeMinSymbolContrast
    {
        get => _barcodeMinSymbolContrast;
        set
        {
            float clamped = Math.Clamp(value, 0.05f, 0.90f);
            if (Set(ref _barcodeMinSymbolContrast, clamped))
            {
                NotifyOptionValidationChanged();
            }
        }
    }

    public float PhotometricSlantAngleDeg
    {
        get => _photometricSlantAngleDeg;
        set
        {
            float clamped = Math.Clamp(value, 15.0f, 75.0f);
            if (Set(ref _photometricSlantAngleDeg, clamped))
            {
                NotifyOptionValidationChanged();
            }
        }
    }

    public float PhotometricCurvatureThreshold
    {
        get => _photometricCurvatureThreshold;
        set
        {
            float clamped = Math.Clamp(value, 0.01f, 0.45f);
            if (Set(ref _photometricCurvatureThreshold, clamped))
            {
                NotifyOptionValidationChanged();
            }
        }
    }

    public int PhotometricMaxDefectPixels
    {
        get => _photometricMaxDefectPixels;
        set
        {
            int clamped = Math.Clamp(value, 1, 500);
            if (Set(ref _photometricMaxDefectPixels, clamped))
            {
                NotifyOptionValidationChanged();
            }
        }
    }

    public float GdtAngleToleranceDeg
    {
        get => _gdtAngleToleranceDeg;
        set
        {
            float clamped = Math.Clamp(value, 0.2f, 15.0f);
            if (Set(ref _gdtAngleToleranceDeg, clamped))
            {
                NotifyOptionValidationChanged();
            }
        }
    }

    public float GdtConcentricityLimitPx
    {
        get => _gdtConcentricityLimitPx;
        set
        {
            float clamped = Math.Clamp(value, 0.2f, 20.0f);
            if (Set(ref _gdtConcentricityLimitPx, clamped))
            {
                NotifyOptionValidationChanged();
            }
        }
    }

    public float GdtDistanceTolerancePx
    {
        get => _gdtDistanceTolerancePx;
        set
        {
            float clamped = Math.Clamp(value, 0.2f, 30.0f);
            if (Set(ref _gdtDistanceTolerancePx, clamped))
            {
                NotifyOptionValidationChanged();
            }
        }
    }

    public float GdtGradientThreshold
    {
        get => _gdtGradientThreshold;
        set
        {
            float clamped = Math.Clamp(value, 2.0f, 120.0f);
            if (Set(ref _gdtGradientThreshold, clamped))
            {
                NotifyOptionValidationChanged();
            }
        }
    }

    public bool ShowOverlay
    {
        get => _showOverlay;
        set
        {
            if (Set(ref _showOverlay, value))
            {
                Notify(nameof(Overlay));
                Changed?.Invoke();
            }
        }
    }

    public string ParameterPreviewSummary => _selectedAlgorithm switch
    {
        InspectionSuiteAlgorithmKind.Metrology =>
            $"모드={_metrologyMode} · 극성={_polarityMode} · σ={_gaussianSigma:F2} · 대비≥{_minimumContrast:F1} · 레이크/스포크={_caliperRakeCount} · 투영폭={_caliperProjectionWidth:F0}px",
        InspectionSuiteAlgorithmKind.GoldenVariation =>
            $"표준편차 배수 k={_variationStdMultiplier:F1} · 에지 지터 허용 c={_variationEdgeMultiplier:F2} · 최소 결함면적={_goldenMinDefectAreaPixels}px",
        InspectionSuiteAlgorithmKind.BlobRle =>
            $"이진화 임계 밝기={_thresholdIntensity} GV · 모폴로지 반경 R={_morphologyRadius} px · 최소 블롭면적={_blobMinimumAreaPixels}px",
        InspectionSuiteAlgorithmKind.Curvilinear =>
            $"Steger σ={_gaussianSigma:F2} · 최소대비={_minimumContrast:F1} · 비드 기준폭={_nominalBeadWidth:F1} px",
        InspectionSuiteAlgorithmKind.FewShot =>
            $"OOD 기각 배수={_fewShotOodSigmaMultiplier:F1}σ · 클래스당 서브클러스터 K={_fewShotMaxSubClusters}개 · SimpleShot + Ledoit-Wolf",
        InspectionSuiteAlgorithmKind.PatchAnomaly =>
            $"PaDiM 패치 크기={_patchSize}×{_patchSize} px · 공분산 정규화 λ={_padimRegularizationLambda:F3}",
        InspectionSuiteAlgorithmKind.Calibration =>
            $"격자 피치={_gridPitchMm:F2} mm · 방사 왜곡 k1={_lensK1:F4}",
        InspectionSuiteAlgorithmKind.CornerAlignment =>
            $"σ={_gaussianSigma:F2} · 대비≥{_minimumContrast:F1} · 레이크={_caliperRakeCount}개 · 모따기 회피={_cornerApexIgnoreMargin:F0}px · 직각허용=±{_cornerOrthogonalityToleranceDeg:F1}° · RANSAC={_cornerRansacThresholdPixels:F2}px",
        InspectionSuiteAlgorithmKind.IndustrialOcrOcv =>
            $"문자인식 임계={_ocrAcceptThreshold:F2} · OCV 품질임계={_ocrMinQualityScore:F2} · 전경대비={_ocrForegroundContrast:F1} GV · 예상피치={_ocrExpectedCharPitch}px · 도트연결 R={_ocrDotClosingRadius}px",
        InspectionSuiteAlgorithmKind.LightweightOcr =>
            $"CTC Beam 폭={_lightweightOcrBeamWidth} · 열 스트라이드={_lightweightOcrColumnStride}px · 적응 대비 오프셋={_lightweightOcrContrastOffset:F1} GV · 최소 신뢰도={_lightweightOcrMinConfidence:F2}",
        InspectionSuiteAlgorithmKind.AutoTeaching =>
            $"기하 매칭 최소점수={_autoTeachMinGeometricScore:F2} · 64B 서명 코사인={_autoTeachMinAppearanceCosine:F2} · 마할라노비스 상한={_autoTeachMaxMahalanobis:F1}",
        InspectionSuiteAlgorithmKind.CpuDeepOcr =>
            $"초해상도={_deepOcrSuperResScale:F2}x · 언샤프닝 α={_deepOcrUnsharpGain:F2} · 앙상블 빔 폭={_deepOcrEnsembleBeamWidth} · 대비 오프셋={_deepOcrContrastOffset:F1} GV · 신뢰도≥{_deepOcrMinConfidence:F2}",
        InspectionSuiteAlgorithmKind.PreprocessingShadingAndRecon =>
            $"다항식 목표 배경={_shadingTargetBaseGray} GV · 비등방성 확산 κ={_anisotropicDiffusionKappa:F1} · 재구성 마커 R={_reconstructionMarkerRadius} px",
        InspectionSuiteAlgorithmKind.AdvancedMetrologyEllipse =>
            $"복합 목표 폭={_compositeExpectedSpanPx:F1}px · 타원 스포크={_ellipseSpokeCount}개 · 허용 RMS={_ellipseMaxRmsErrorPx:F2}px · σ={_gaussianSigma:F2} · 대비≥{_minimumContrast:F1}",
        InspectionSuiteAlgorithmKind.AdvancedBlobWatershed =>
            $"워터셰드 임계={_watershedThresholdIntensity} GV · EDT 피크 간격={_watershedMinPeakDistancePx:F1}px · 최소 면적={_watershedMinAreaPixels}px",
        InspectionSuiteAlgorithmKind.DataMatrix2DReader =>
            $"ECC 200 최소 ISO 대비={_dataMatrixMinIsoContrast:F2} · Reed-Solomon 최대 정정 부호어={_dataMatrixMaxEccErrors}개 · GF(2^8) p=301",
        InspectionSuiteAlgorithmKind.PreprocessingFftAndGabor =>
            $"FFT 노치/Gabor 대역폭 σ={_fftNotchOrGaborSigma:F2} · 스펙트럼 피크 배수={_fftPeakThresholdRatio:F1}x · 호모모픽 γH={_homomorphicGammaHigh:F2}",
        InspectionSuiteAlgorithmKind.Barcode1DReader =>
            $"Code 128B 다중 스캔라인={_barcodeScanlineCount}줄 · 최소 심볼 대비(SC)={_barcodeMinSymbolContrast:F2} · Modulo-103 검증",
        InspectionSuiteAlgorithmKind.PhotometricStereo =>
            $"Woodham 조명 고도각={_photometricSlantAngleDeg:F0}° · 3D 곡률 임계(|H|)={_photometricCurvatureThreshold:F3} · 결함 화소 한계={_photometricMaxDefectPixels}px",
        InspectionSuiteAlgorithmKind.GeometricTolerancing =>
            $"GD&T 직교도 공차=±{_gdtAngleToleranceDeg:F1}° · 동심도 한계 Δc≤{_gdtConcentricityLimitPx:F2}px · 거리 공차=±{_gdtDistanceTolerancePx:F1}px · 에지 대비≥{_gdtGradientThreshold:F1}",
        _ => string.Empty
    };

    public string OptionsError
    {
        get
        {
            if (_hasInputErrors)
            {
                return "숫자 형식이 올바르지 않은 입력 항목이 있습니다.";
            }
            if (!float.IsFinite(_gaussianSigma) || _gaussianSigma < 0.4f || _gaussianSigma > 10.0f)
            {
                return "가우시안 시그마(σ)는 0.4 ~ 10.0 범위여야 합니다.";
            }
            if (!float.IsFinite(_minimumContrast) || _minimumContrast < 1.0f || _minimumContrast > 250.0f)
            {
                return "최소 에지 대비(Contrast)는 1.0 ~ 250.0 범위여야 합니다.";
            }
            if (_thresholdIntensity < 1 || _thresholdIntensity > 255)
            {
                return "RLE 이진화 임계값은 1 ~ 255 범위여야 합니다.";
            }
            if (_morphologyRadius < 0 || _morphologyRadius > 16)
            {
                return "모폴로지 반경은 0 ~ 16 범위여야 합니다.";
            }
            return string.Empty;
        }
    }

    public void SetInputError(bool hasError)
    {
        if (Set(ref _hasInputErrors, hasError))
        {
            NotifyOptionValidationChanged();
        }
    }

    public void Update(InspectionUpdate update)
    {
        if (_disposed)
        {
            return;
        }

        var state = update.State;
        bool imageChanged = _generation != update.Generation || !Equals(_image, state.Image);
        bool roisOrSelectionChanged = !ReferenceEquals(_lastRoisRef, state.Rois)
            || !ReferenceEquals(_lastSelectionRef, state.Selection)
            || _transformPreview != update.TransformPreview;
        bool toolBecameAvailable = !_toolAvailable && update.ToolAvailable;
        bool wasCanExecute = _canExecute;

        _state = state;
        _lastRoisRef = state.Rois;
        _lastSelectionRef = state.Selection;
        _transformPreview = update.TransformPreview;
        _image = state.Image;
        _generation = update.Generation;
        _live = update.Live;
        _toolAvailable = update.ToolAvailable;
        _canExecute = update.CanExecute && update.ToolAvailable;

        if (!update.ToolAvailable && !imageChanged)
        {
            if (wasCanExecute != _canExecute)
            {
                RefreshCommands();
            }
            return;
        }

        if (imageChanged)
        {
            _epoch++;
            _cancellation?.Cancel();
            CancelLivePreview();
            _referenceRois.Clear();
            RegisteredReferenceItems.Clear();
            _overlay = null;
            _livePreview = null;
            LastOutput = null;
            ReferenceSummary = "등록된 기준 ROI 없음 (미등록 시 첫 번째 대상 ROI를 기준으로 자동 학습)";
            Results = "실행 결과 없음";
            Status = _image is null
                ? "상단 '현재 알고리즘 샘플 열기'를 누르거나 이미지를 열어 주세요."
                : "ROI를 선택·조절하면 실시간 프리뷰가 표시됩니다.";
            Notify(nameof(ReferenceSummary));
            Notify(nameof(HasRegisteredReferences));
            Notify(nameof(Results));
            Notify(nameof(Status));
            if (_image is not null)
            {
                UpdateGeometricGuideAndSchedulePreview(triggerAsyncMeasurement: SynchronizationContext.Current is not null);
            }
            else
            {
                Notify(nameof(Overlay));
                Changed?.Invoke();
            }
        }
        else if (roisOrSelectionChanged || toolBecameAvailable)
        {
            if (_transformPreview is not null)
            {
                // ROI 드래그/회전/크기 조절 중에는 기존 고정 결과를 지우고 실시간 기하 가이드로 즉시 전환
                _overlay = null;
            }
            UpdateGeometricGuideAndSchedulePreview(triggerAsyncMeasurement: SynchronizationContext.Current is not null);
        }

        RefreshCommands();
    }

    private void RegisterSelectedRois(int classLabel)
    {
        var selected = GetSelectedLeafRois();
        if (selected.Count == 0)
        {
            return;
        }

        foreach (var roi in selected)
        {
            var input = ToRoiInput(roi, classLabel);
            int idx = _referenceRois.FindIndex(item => item.RoiId == roi.Id);
            if (idx >= 0)
            {
                _referenceRois[idx] = input;
            }
            else
            {
                _referenceRois.Add(input);
            }
            EmitSuiteReferenceAssignment(input);
        }

        RebuildRegisteredReferenceItems();
        Status = _selectedAlgorithm == InspectionSuiteAlgorithmKind.AutoTeaching
            ? $"{selected.Count}개 부품 ROI를 멀티파츠 모델 풀(64B 서명 + 하위 검사 ROI 3개)에 학습·등록했습니다."
            : $"{selected.Count}개 ROI를 {(classLabel == 0 ? "정상/기준(Good)" : "불량(Defect)")} 샘플로 등록했습니다.";
        Notify(nameof(Status));
        UpdateGeometricGuideAndSchedulePreview(triggerAsyncMeasurement: SynchronizationContext.Current is not null);
        RefreshCommands();
    }

    public void RegisterPresetReferenceRois(IReadOnlyList<Guid> normalRoiIds, IReadOnlyList<Guid> defectRoiIds)
    {
        _referenceRois.Clear();
        foreach (Guid id in normalRoiIds)
        {
            if (_state.Rois.TryGetValue(id, out var roi) && !_state.IsGroupRoi(id))
            {
                var input = ToRoiInput(roi, classLabel: 0);
                _referenceRois.Add(input);
                EmitSuiteReferenceAssignment(input);
            }
        }

        foreach (Guid id in defectRoiIds)
        {
            if (_state.Rois.TryGetValue(id, out var roi) && !_state.IsGroupRoi(id))
            {
                var input = ToRoiInput(roi, classLabel: 1);
                _referenceRois.Add(input);
                EmitSuiteReferenceAssignment(input);
            }
        }

        RebuildRegisteredReferenceItems(isPreset: true);
        RefreshCommands();
    }

    private void RemoveRegisteredReference(Guid roiId)
    {
        int removed = _referenceRois.RemoveAll(r => r.RoiId == roiId);
        if (removed <= 0)
        {
            return;
        }

        RebuildRegisteredReferenceItems();
        Status = "선택한 등록 모델/기준 ROI를 풀에서 제거했습니다.";
        Notify(nameof(Status));
        UpdateGeometricGuideAndSchedulePreview(triggerAsyncMeasurement: SynchronizationContext.Current is not null);
        RefreshCommands();
    }

    private void RebuildRegisteredReferenceItems(bool isPreset = false)
    {
        RegisteredReferenceItems.Clear();
        bool isAutoTeach = _selectedAlgorithm == InspectionSuiteAlgorithmKind.AutoTeaching;
        for (int i = 0; i < _referenceRois.Count; i++)
        {
            var r = _referenceRois[i];
            double angleDeg = r.AngleRadians * 180.0 / Math.PI;
            string roleBadge = isAutoTeach
                ? $"파츠모델 #{i + 1}"
                : (r.ClassLabel == 0 ? "정상(OK)" : "불량(NG)");
            string roleAccent = isAutoTeach
                ? "#38BDF8"
                : (r.ClassLabel == 0 ? "#34D399" : "#F87171");
            string geomSummary = $"중심=({r.Center.X:F1}, {r.Center.Y:F1}) · 크기={r.Width:F0}×{r.Height:F0}px · θ={angleDeg:+0.0;-0.0;0.0}°";
            string childSummary = isAutoTeach
                ? "64B 회전불변 서명 · 하위 ROI 3개(좌측핀A / 우측핀B / 중앙바디) 자동전파"
                : (r.ClassLabel == 0 ? "정상(Golden/Good) 기준 샘플" : "불량(Defect) 분류 기준 샘플");
            Guid targetId = r.RoiId;
            RegisteredReferenceItems.Add(new SuiteRegisteredReferenceItem(
                RoiId: targetId,
                Name: r.Name,
                RoleBadge: roleBadge,
                RoleAccent: roleAccent,
                GeometrySummary: geomSummary,
                ChildTargetSummary: childSummary,
                RemoveCommand: new RelayCommand(() => RemoveRegisteredReference(targetId))));
        }

        int normalCount = _referenceRois.Count(r => r.ClassLabel == 0);
        int defectCount = _referenceRois.Count(r => r.ClassLabel != 0);
        if (_referenceRois.Count == 0)
        {
            ReferenceSummary = isAutoTeach
                ? "등록된 파츠 모델 없음 (캔버스 ROI 선택 후 '＋ 선택 ROI 파츠 학습 등록' 클릭 시 모델 풀에 추가됨)"
                : "등록된 기준 ROI 없음 (미등록 시 첫 번째 대상 ROI를 기준으로 자동 학습)";
        }
        else if (isAutoTeach)
        {
            ReferenceSummary = $"{(isPreset ? "프리셋 " : string.Empty)}멀티파츠 모델 풀: 등록 파츠 {_referenceRois.Count}종 (각 파츠당 하위 검사 ROI 3개 자동 전파)";
        }
        else
        {
            ReferenceSummary = $"{(isPreset ? "프리셋 기준 ROI 자동 등록됨: " : "등록 기준 ROI ")}총 {_referenceRois.Count}개 (정상 {normalCount}개 · 불량 {defectCount}개)";
        }

        Notify(nameof(ReferenceSummary));
        Notify(nameof(HasRegisteredReferences));
    }

    private void RequestSampleLoad(InspectionSuiteAlgorithmKind kind)
    {
        SelectedAlgorithm = kind;
        SampleLoadRequested?.Invoke(kind);
    }

    private void ClearAllReferences()
    {
        _referenceRois.Clear();
        RegisteredReferenceItems.Clear();
        _overlay = null;
        _livePreview = null;
        LastOutput = null;
        ReferenceSummary = _selectedAlgorithm == InspectionSuiteAlgorithmKind.AutoTeaching
            ? "등록된 파츠 모델 없음 (캔버스 ROI 선택 후 '＋ 선택 ROI 파츠 학습 등록' 클릭 시 모델 풀에 추가됨)"
            : "등록된 기준 ROI 없음 (미등록 시 첫 번째 대상 ROI를 기준으로 자동 학습)";
        Results = "실행 결과 없음";
        Status = "등록된 모델 풀/기준 ROI와 결과 오버레이를 초기화했습니다.";
        Notify(nameof(ReferenceSummary));
        Notify(nameof(HasRegisteredReferences));
        Notify(nameof(Results));
        Notify(nameof(Status));
        Notify(nameof(Overlay));
        RefreshCommands();
        Changed?.Invoke();
    }

    private async Task ExecuteRoisAsync(bool selectedOnly)
    {
        if (_image is not { } image)
        {
            return;
        }

        var selectedRois = selectedOnly ? GetSelectedLeafRois() : [];
        var rois = selectedRois.Count > 0 ? selectedRois : GetAllLeafRois();
        if (rois.Count == 0)
        {
            PresentWarning("검사할 리프 ROI가 없습니다. 캔버스에 ROI를 그리거나 선택해 주세요.");
            return;
        }

        var targets = rois.Select(roi => ToRoiInput(roi, classLabel: 0)).ToArray();
        var refs = _referenceRois.ToArray();
        var parameters = BuildParameters();
        var algorithm = _selectedAlgorithm;

        long epoch = ++_epoch;
        CancelLivePreview();
        using var cts = BeginBusy($"{InspectionSuiteService.GetAlgorithmShortName(algorithm)} 실행 중… (ROI {targets.Length}개)");
        try
        {
            var output = await _service.ExecuteRoiInspectionAsync(image, _live, algorithm, targets, refs, parameters, cts.Token);
            if (epoch != _epoch || _disposed)
            {
                return;
            }

            ApplyExecutionOutput(output);
        }
        catch (OperationCanceledException)
        {
            if (epoch == _epoch)
            {
                Status = "검사가 취소되었습니다.";
                Notify(nameof(Status));
            }
        }
        finally
        {
            EndBusy(cts);
        }
    }

    private async Task RunMaster8kSuiteInternalAsync()
    {
        long epoch = ++_epoch;
        CancelLivePreview();
        using var cts = BeginBusy("8K(8192×8192) 마스터 합성 영상 생성 및 7종 알고리즘 전수 검증 중…");
        try
        {
            var output = await _service.RunMaster8kSuiteAsync(cts.Token);
            if (epoch != _epoch || _disposed)
            {
                return;
            }

            ApplyExecutionOutput(output);
        }
        catch (OperationCanceledException)
        {
            if (epoch == _epoch)
            {
                Status = "8K 7종 전수 검증이 취소되었습니다.";
                Notify(nameof(Status));
            }
        }
        finally
        {
            EndBusy(cts);
        }
    }

    private void ApplyExecutionOutput(InspectionSuiteExecutionOutput output)
    {
        LastOutput = output;
        _overlay = BuildResultOverlayFromOutput(output, isLivePreview: false);

        Status = output.SummaryTitle;
        Results = output.DetailedReport;
        double perItemMs = output.Items.Length > 0 ? output.TotalMilliseconds / output.Items.Length : output.TotalMilliseconds;
        foreach (var item in output.Items)
        {
            EmitSuiteExecutionResult(item, output.Algorithm, perItemMs);
        }
        Notify(nameof(LastOutput));
        Notify(nameof(Status));
        Notify(nameof(Results));
        Notify(nameof(Overlay));
        Locate.Refresh();
        Changed?.Invoke();
    }

    private static ResultOverlay BuildResultOverlayFromOutput(InspectionSuiteExecutionOutput output, bool isLivePreview)
    {
        var markers = ImmutableArray.CreateBuilder<MatchMarker>(output.Items.Length);

        foreach (var item in output.Items)
        {
            Vector4 markerColor = item.IsPass ? PassColor : FailColor;
            var diagGroups = ImmutableArray.CreateBuilder<DiagnosticPointGroup>();
            if (!item.PrimaryPoints.IsEmpty)
            {
                diagGroups.Add(new DiagnosticPointGroup(PrimaryFeatureColor, item.PrimaryPoints, HalfSize: 2.8));
            }
            if (!item.SecondaryPoints.IsEmpty)
            {
                diagGroups.Add(new DiagnosticPointGroup(SecondaryFeatureColor, item.SecondaryPoints, HalfSize: 2.4));
            }
            if (!item.DefectOrOutlierPoints.IsEmpty)
            {
                diagGroups.Add(new DiagnosticPointGroup(DefectFeatureColor, item.DefectOrOutlierPoints, HalfSize: 2.8));
            }

            var guides = item.GuideContours.IsDefault ? [] : item.GuideContours;
            markers.Add(new MatchMarker(
                Bounds: item.Bounds,
                ReferencePoint: item.ReferencePoint,
                AngleRadians: item.AngleRadians,
                Label: $"{item.Name}: {item.BadgeLabel}",
                Color: markerColor,
                Features: item.PrimaryPoints)
            {
                InnerContours = guides,
                InnerContourColor = GuideInnerColor,
                InnerContourThickness = 1.25,
                InnerContourDashLength = 4.0,
                InnerContourGapLength = 3.0,
                AdditionalContours = item.AdditionalContours,
                DiagnosticGroups = diagGroups.ToImmutable(),
                KeyFeatures = item.PrimaryPoints,
                UnmatchedFeatures = item.DefectOrOutlierPoints,
                BoundsColor = markerColor,
                BoundsThickness = isLivePreview ? 1.8 : 2.2
            });
        }

        bool allPass = output.Items.All(item => item.IsPass);
        string summaryPrefix = isLivePreview ? "[실시간 프리뷰] " : string.Empty;
        return new ResultOverlay(
            Summary: summaryPrefix + output.SummaryTitle,
            Color: allPass ? PassColor : FailColor,
            SearchBoundary: [],
            Matches: markers.MoveToImmutable(),
            ShowFeatures: true);
    }

    private void UpdateGeometricGuideAndSchedulePreview(bool triggerAsyncMeasurement = true)
    {
        if (_disposed || !_toolAvailable)
        {
            return;
        }

        var previewRois = GetPreviewTargetRois();
        if (previewRois.Count == 0)
        {
            if (_livePreview is not null)
            {
                _livePreview = null;
                Notify(nameof(Overlay));
                Changed?.Invoke();
            }
            return;
        }

        var parameters = BuildParameters();
        var targets = previewRois.Select(roi => ToRoiInput(roi, classLabel: 0)).ToArray();
        bool canScheduleAsyncMeasurement = triggerAsyncMeasurement && _autoPreview && _showOverlay && IsIdle && _image is not null && OptionsError.Length == 0;

        // 1. 0ms 동기 기하 가이드 오버레이 생성 (ROI 드래그·회전 중이거나 최초 프리뷰 대기 시 60FPS로 캘리퍼 스캔선/레이크/스포크 표시)
        //    단, 파라미터 슬라이더 조절 중 이미 실시간 프리뷰 결과가 화면에 떠 있는 경우에는
        //    중간에 빈 가이드 오버레이로 덮어써서 ROI 내부가 깜빡이는 현상(Flickering)을 원천 차단하고,
        //    백그라운드 계측이 완료될 때 새 결과 오버레이로 원자적(Atomic) 교체한다.
        bool showGuideImmediately = _overlay is null && (_livePreview is null || _transformPreview is not null || !canScheduleAsyncMeasurement);
        if (showGuideImmediately)
        {
            var guideMarkers = ImmutableArray.CreateBuilder<MatchMarker>(targets.Length);
            foreach (var target in targets)
            {
                var guides = InspectionSuiteService.BuildGeometricGuidesForRoi(target, _selectedAlgorithm, parameters);
                guideMarkers.Add(new MatchMarker(
                    Bounds: target.Outline,
                    ReferencePoint: target.Center,
                    AngleRadians: target.AngleRadians,
                    Label: $"{target.Name} · 가이드 미리보기",
                    Color: GuideBoundsColor,
                    Features: [])
                {
                    InnerContours = guides,
                    InnerContourColor = GuideInnerColor,
                    InnerContourThickness = 1.35,
                    InnerContourDashLength = 4.0,
                    InnerContourGapLength = 3.0,
                    BoundsColor = GuideBoundsColor,
                    BoundsThickness = 1.4,
                    BoundsDashLength = 6.0,
                    BoundsGapLength = 4.0
                });
            }

            _livePreview = new ResultOverlay(
                Summary: $"[가이드 미리보기] {InspectionSuiteService.GetAlgorithmShortName(_selectedAlgorithm)} · {ParameterPreviewSummary}",
                Color: GuideBoundsColor,
                SearchBoundary: [],
                Matches: guideMarkers.MoveToImmutable(),
                ShowFeatures: true);
            Notify(nameof(Overlay));
            Locate.Refresh();
            Changed?.Invoke();
        }

        // 2. 실시간 자동 계측 워커 예약
        if (canScheduleAsyncMeasurement)
        {
            ScheduleLivePreview(targets, parameters);
        }
    }

    private void ScheduleLivePreview(InspectionSuiteRoiInput[] targets, InspectionSuiteParameters parameters)
    {
        if (_image is not { } image || targets.Length == 0)
        {
            return;
        }

        long epoch = Interlocked.Increment(ref _previewEpoch);
        var refs = _referenceRois.ToArray();
        Volatile.Write(
            ref _pendingPreview,
            new LivePreviewRequest(
                image,
                _live,
                _selectedAlgorithm,
                targets,
                refs,
                parameters,
                SynchronizationContext.Current,
                epoch,
                _generation));

        if (_previewPump is null || _previewPump.IsCompleted)
        {
            _previewPump = RunLivePreviewPumpAsync();
        }
    }

    private async Task RunLivePreviewPumpAsync()
    {
        while (!_disposed)
        {
            var request = Interlocked.Exchange(ref _pendingPreview, null);
            if (request is null)
            {
                break;
            }

            if (_disposed || !IsIdle || !_autoPreview || !_showOverlay || !_toolAvailable ||
                request.Generation != _generation || request.Algorithm != _selectedAlgorithm)
            {
                continue;
            }

            using var cts = new CancellationTokenSource();
            _previewCancellation = cts;
            try
            {
                var output = await _service.ExecuteRoiInspectionAsync(
                    request.Image,
                    request.Live,
                    request.Algorithm,
                    request.Targets,
                    request.References,
                    request.Parameters,
                    cts.Token).ConfigureAwait(false);

                if (cts.Token.IsCancellationRequested || _disposed || !IsIdle || !_autoPreview || !_showOverlay ||
                    request.Generation != _generation || request.Algorithm != _selectedAlgorithm || _overlay is not null)
                {
                    continue;
                }

                var overlay = BuildResultOverlayFromOutput(output, isLivePreview: true);
                PublishLivePreview(request.UiContext, request.Epoch, request.Generation, request.Algorithm, output, overlay);
            }
            catch
            {
                // 실시간 프리뷰 중 일시적인 경계/취소 예외는 무시하고 기하 가이드를 유지한다.
            }
            finally
            {
                if (ReferenceEquals(_previewCancellation, cts))
                {
                    _previewCancellation = null;
                }
            }
        }
    }

    private void PublishLivePreview(
        SynchronizationContext? uiContext,
        long previewEpoch,
        long previewGeneration,
        InspectionSuiteAlgorithmKind algorithm,
        InspectionSuiteExecutionOutput output,
        ResultOverlay overlay)
    {
        void Apply()
        {
            if (_disposed || !IsIdle || !_autoPreview || !_showOverlay || !_toolAvailable ||
                previewGeneration != _generation || previewEpoch < _lastPublishedPreviewEpoch ||
                algorithm != _selectedAlgorithm || _overlay is not null)
            {
                return;
            }

            _lastPublishedPreviewEpoch = previewEpoch;
            _livePreview = overlay;
            LastOutput = output;
            Status = $"[실시간 프리뷰] {output.SummaryTitle}";
            Results = output.DetailedReport;
            Notify(nameof(LastOutput));
            Notify(nameof(Status));
            Notify(nameof(Results));
            Notify(nameof(Overlay));
            Locate.Refresh();
            Changed?.Invoke();
        }

        if (uiContext is not null && SynchronizationContext.Current != uiContext)
        {
            uiContext.Post(_ => Apply(), null);
        }
        else
        {
            Apply();
        }
    }

    private void CancelLivePreview()
    {
        Interlocked.Increment(ref _previewEpoch);
        Interlocked.Exchange(ref _pendingPreview, null);
        _previewCancellation?.Cancel();
    }

    private void EmitSuiteReferenceAssignment(InspectionSuiteRoiInput input)
    {
        bool isNormal = input.ClassLabel == 0;
        RoiAssignmentRecorded?.Invoke(new RoiAlgorithmAssignment(
            RoiId: input.RoiId,
            RoiName: input.Name,
            Tool: InspectionToolKind.InspectionSuite,
            PatternMethod: null,
            SuiteAlgorithm: _selectedAlgorithm,
            AlgorithmBadge: RoiAlgorithmAssignment.FormatAlgorithmBadge(InspectionToolKind.InspectionSuite, null, _selectedAlgorithm),
            AlgorithmAccent: RoiAlgorithmAssignment.GetAlgorithmAccent(InspectionToolKind.InspectionSuite, null, _selectedAlgorithm),
            ParameterSummary: ParameterPreviewSummary,
            VerdictBadge: isNormal ? "기준:정상" : "기준:불량",
            VerdictAccent: isNormal ? "#34D399" : "#FBBF24",
            LastResultSummary: $"{InspectionSuiteService.GetAlgorithmShortName(_selectedAlgorithm)} 기준 샘플 ({(isNormal ? "정상" : "불량")})"));
    }

    private void EmitSuiteExecutionResult(
        InspectionSuiteItemResult item,
        InspectionSuiteAlgorithmKind algorithm,
        double elapsedMilliseconds)
    {
        string badge = RoiAlgorithmAssignment.FormatAlgorithmBadge(InspectionToolKind.InspectionSuite, null, algorithm);
        string accent = RoiAlgorithmAssignment.GetAlgorithmAccent(InspectionToolKind.InspectionSuite, null, algorithm);
        var verdict = item.IsPass ? InspectionVerdict.Pass : InspectionVerdict.Fail;
        string verdictBadge = item.IsPass ? "PASS" : "NG";
        string verdictAccent = item.IsPass ? "#34D399" : "#F87171";
        string scoreSummary = item.BadgeLabel;
        string poseSummary = $"X={item.ReferencePoint.X:F1}, Y={item.ReferencePoint.Y:F1} · {item.DetailLine}";
        BoundsD? bounds = item.Bounds.IsEmpty ? null : BoundsD.FromPoints(item.Bounds);

        if (item.RoiId != Guid.Empty)
        {
            RoiAssignmentRecorded?.Invoke(new RoiAlgorithmAssignment(
                RoiId: item.RoiId,
                RoiName: item.Name,
                Tool: InspectionToolKind.InspectionSuite,
                PatternMethod: null,
                SuiteAlgorithm: algorithm,
                AlgorithmBadge: badge,
                AlgorithmAccent: accent,
                ParameterSummary: ParameterPreviewSummary,
                VerdictBadge: verdictBadge,
                VerdictAccent: verdictAccent,
                LastResultSummary: $"{verdictBadge} · {item.BadgeLabel} · {item.DetailLine}"));
        }

        StructuredLogRecorded?.Invoke(new StructuredInspectionLogEntry(
            EntryId: Guid.NewGuid(),
            Timestamp: DateTimeOffset.Now,
            RoiId: item.RoiId == Guid.Empty ? null : item.RoiId,
            RoiName: item.Name,
            Tool: InspectionToolKind.InspectionSuite,
            AlgorithmBadge: badge,
            Verdict: verdict,
            VerdictBadge: verdictBadge,
            VerdictAccent: verdictAccent,
            ScoreSummary: scoreSummary,
            PoseSummary: poseSummary,
            ElapsedMilliseconds: elapsedMilliseconds,
            TargetBounds: bounds));
    }

    private InspectionSuiteParameters BuildParameters() => new(
        GaussianSigma: _gaussianSigma,
        MinimumContrast: _minimumContrast,
        ThresholdIntensity: _thresholdIntensity,
        MorphologyRadius: _morphologyRadius,
        VariationStdMultiplier: _variationStdMultiplier,
        VariationEdgeMultiplier: _variationEdgeMultiplier,
        NominalBeadWidth: _nominalBeadWidth,
        PatchSize: _patchSize,
        GridPitchMm: _gridPitchMm,
        LensK1: _lensK1,
        MetrologyMode: _metrologyMode,
        PolarityMode: _polarityMode,
        CaliperRakeCount: _caliperRakeCount,
        CaliperProjectionWidth: _caliperProjectionWidth,
        GoldenMinDefectAreaPixels: _goldenMinDefectAreaPixels,
        BlobMinimumAreaPixels: _blobMinimumAreaPixels,
        FewShotOodSigmaMultiplier: _fewShotOodSigmaMultiplier,
        FewShotMaxSubClusters: _fewShotMaxSubClusters,
        PadimRegularizationLambda: _padimRegularizationLambda,
        CornerApexIgnoreMargin: _cornerApexIgnoreMargin,
        CornerRansacThresholdPixels: _cornerRansacThresholdPixels,
        CornerOrthogonalityToleranceDeg: _cornerOrthogonalityToleranceDeg,
        OcrAcceptThreshold: _ocrAcceptThreshold,
        OcrMinQualityScore: _ocrMinQualityScore,
        OcrForegroundContrast: _ocrForegroundContrast,
        OcrExpectedCharPitch: _ocrExpectedCharPitch,
        LightweightOcrBeamWidth: _lightweightOcrBeamWidth,
        LightweightOcrColumnStride: _lightweightOcrColumnStride,
        LightweightOcrMinConfidence: _lightweightOcrMinConfidence,
        AutoTeachMinGeometricScore: _autoTeachMinGeometricScore,
        AutoTeachMinAppearanceCosine: _autoTeachMinAppearanceCosine,
        AutoTeachMaxMahalanobis: _autoTeachMaxMahalanobis,
        OcrDotClosingRadius: _ocrDotClosingRadius,
        LightweightOcrContrastOffset: _lightweightOcrContrastOffset,
        DeepOcrSuperResScale: _deepOcrSuperResScale,
        DeepOcrUnsharpGain: _deepOcrUnsharpGain,
        DeepOcrEnsembleBeamWidth: _deepOcrEnsembleBeamWidth,
        DeepOcrContrastOffset: _deepOcrContrastOffset,
        DeepOcrMinConfidence: _deepOcrMinConfidence,
        ShadingTargetBaseGray: _shadingTargetBaseGray,
        AnisotropicDiffusionKappa: _anisotropicDiffusionKappa,
        ReconstructionMarkerRadius: _reconstructionMarkerRadius,
        CompositeExpectedSpanPx: _compositeExpectedSpanPx,
        EllipseSpokeCount: _ellipseSpokeCount,
        EllipseMaxRmsErrorPx: _ellipseMaxRmsErrorPx,
        WatershedThresholdIntensity: _watershedThresholdIntensity,
        WatershedMinPeakDistancePx: _watershedMinPeakDistancePx,
        WatershedMinAreaPixels: _watershedMinAreaPixels,
        DataMatrixMinIsoContrast: _dataMatrixMinIsoContrast,
        DataMatrixMaxEccErrors: _dataMatrixMaxEccErrors,
        FftNotchOrGaborSigma: _fftNotchOrGaborSigma,
        FftPeakThresholdRatio: _fftPeakThresholdRatio,
        HomomorphicGammaHigh: _homomorphicGammaHigh,
        BarcodeScanlineCount: _barcodeScanlineCount,
        BarcodeMinSymbolContrast: _barcodeMinSymbolContrast,
        PhotometricSlantAngleDeg: _photometricSlantAngleDeg,
        PhotometricCurvatureThreshold: _photometricCurvatureThreshold,
        PhotometricMaxDefectPixels: _photometricMaxDefectPixels,
        GdtAngleToleranceDeg: _gdtAngleToleranceDeg,
        GdtConcentricityLimitPx: _gdtConcentricityLimitPx,
        GdtDistanceTolerancePx: _gdtDistanceTolerancePx,
        GdtGradientThreshold: _gdtGradientThreshold);

    private bool HasSelectedLeafRoi()
    {
        if (_state.Selection.Count == 0 || _state.Rois.IsEmpty)
        {
            return false;
        }

        if (_state.Snapshot.Children.IsEmpty && _state.Snapshot.GroupLevels.IsEmpty)
        {
            return true;
        }

        foreach (var id in _state.Selection)
        {
            if (_state.Rois.TryGetValue(id, out var roi) && !roi.IsGroup)
            {
                return true;
            }
        }

        return false;
    }

    private bool HasAnyLeafRoi()
    {
        if (_state.Rois.IsEmpty)
        {
            return false;
        }

        if (_state.Snapshot.Children.IsEmpty && _state.Snapshot.GroupLevels.IsEmpty)
        {
            return true;
        }

        foreach (var roi in _state.Rois.Values)
        {
            if (!roi.IsGroup)
            {
                return true;
            }
        }

        return false;
    }

    private List<Roi> GetPreviewTargetRois()
    {
        if (_state.Selection.Count > 64)
        {
            return [];
        }

        if (_state.Selection.Count > 0)
        {
            var selectedPreview = new List<Roi>(Math.Min(6, _state.Selection.Count));
            foreach (var id in _state.Selection)
            {
                if (_state.Rois.TryGetValue(id, out var roi) && !roi.IsGroup)
                {
                    selectedPreview.Add(roi);
                    if (selectedPreview.Count >= 6)
                    {
                        break;
                    }
                }
            }

            if (selectedPreview.Count > 0)
            {
                return selectedPreview;
            }
        }

        if (_state.Rois.Count > 64)
        {
            return [];
        }

        var all = GetAllLeafRois();
        if (_referenceRois.Count > 0 && _selectedAlgorithm != InspectionSuiteAlgorithmKind.AutoTeaching)
        {
            var nonRef = all.Where(r => _referenceRois.All(refRoi => refRoi.RoiId != r.Id)).ToList();
            if (nonRef.Count > 0)
            {
                return nonRef.Count <= 6 ? nonRef : nonRef.GetRange(0, 6);
            }
        }

        return all.Count <= 6 ? all : all.GetRange(0, 6);
    }

    private List<Roi> GetSelectedLeafRois()
    {
        var list = new List<Roi>();
        foreach (var id in _state.Selection)
        {
            if (_state.Rois.TryGetValue(id, out var roi) && !roi.IsGroup)
            {
                list.Add(roi);
            }
        }
        return list;
    }

    private List<Roi> GetAllLeafRois()
    {
        return _state.Rois.Values
            .Where(roi => !roi.IsGroup)
            .OrderBy(roi => Math.Round(roi.Geometry.Center.Y / 80.0))
            .ThenBy(roi => roi.Geometry.Center.X)
            .ToList();
    }

    private InspectionSuiteRoiInput ToRoiInput(Roi roi, int classLabel)
    {
        var geometry = _transformPreview?.Geometry(roi) ?? roi.Geometry;
        var outline = geometry.Outline.ToImmutableArray();
        double width = Math.Max(8.0, geometry.Width);
        double height = Math.Max(8.0, geometry.Height);
        double angleRad = geometry.Angle * Math.PI / 180.0;
        string name = string.IsNullOrWhiteSpace(roi.Name) ? $"ROI-{roi.Id.ToString()[..4]}" : roi.Name;
        return new InspectionSuiteRoiInput(
            roi.Id,
            name,
            geometry.Center,
            width,
            height,
            angleRad,
            outline,
            classLabel);
    }

    private CancellationTokenSource BeginBusy(string status)
    {
        _cancellation?.Cancel();
        var cts = new CancellationTokenSource();
        _cancellation = cts;
        Status = status;
        Notify(nameof(Status));
        Notify(nameof(IsBusy));
        Notify(nameof(IsIdle));
        RefreshCommands();
        return cts;
    }

    private void EndBusy(CancellationTokenSource cts)
    {
        if (ReferenceEquals(_cancellation, cts))
        {
            _cancellation = null;
            Notify(nameof(IsBusy));
            Notify(nameof(IsIdle));
            RefreshCommands();
        }
    }

    private void PresentWarning(string message)
    {
        Status = $"실행 조건 확인 필요: {message}";
        Notify(nameof(Status));
    }

    private void NotifyAlgorithmSelectionChanged()
    {
        RebuildRegisteredReferenceItems();
        Notify(nameof(SelectedAlgorithmHeaderTitle));
        Notify(nameof(AlgorithmDescription));
        Notify(nameof(AlgorithmUsageGuide));
        Notify(nameof(ActiveHelpManual));
        Notify(nameof(ParameterPreviewSummary));
        Notify(nameof(ReferenceCardTitle));
        Notify(nameof(RegisterNormalButtonText));
        Notify(nameof(IsMetrologySelected));
        Notify(nameof(IsGoldenVariationSelected));
        Notify(nameof(IsBlobRleSelected));
        Notify(nameof(IsCurvilinearSelected));
        Notify(nameof(IsFewShotSelected));
        Notify(nameof(IsPatchAnomalySelected));
        Notify(nameof(IsCalibrationSelected));
        Notify(nameof(IsCornerAlignmentSelected));
        Notify(nameof(IsIndustrialOcrOcvSelected));
        Notify(nameof(IsLightweightOcrSelected));
        Notify(nameof(IsAutoTeachingSelected));
        Notify(nameof(IsCpuDeepOcrSelected));
        Notify(nameof(IsPreprocessingShadingSelected));
        Notify(nameof(IsAdvancedMetrologyEllipseSelected));
        Notify(nameof(IsAdvancedBlobWatershedSelected));
        Notify(nameof(IsDataMatrix2DSelected));
        Notify(nameof(IsPreprocessingFftGaborSelected));
        Notify(nameof(IsBarcode1DSelected));
        Notify(nameof(IsPhotometricStereoSelected));
        Notify(nameof(IsGeometricTolerancingSelected));
        Notify(nameof(UsesMetrologyOrCornerParams));
        Notify(nameof(UsesReferenceRois));
        Notify(nameof(UsesEdgeContrastParams));
    }

    private void NotifyOptionValidationChanged()
    {
        if (_overlay is not null)
        {
            _livePreview = _overlay;
            _overlay = null;
        }
        Notify(nameof(OptionsError));
        Notify(nameof(ParameterPreviewSummary));
        UpdateGeometricGuideAndSchedulePreview();
        RefreshCommands();
    }

    private void RefreshCommands()
    {
        RegisterNormalReference.Refresh();
        RegisterDefectReference.Refresh();
        ClearReferences.Refresh();
        ExecuteSelected.Refresh();
        ExecuteAllRois.Refresh();
        RunMaster8kBenchmark.Refresh();
        LoadSelectedAlgorithmSample.Refresh();
        LoadMetrologySample.Refresh();
        LoadGoldenVariationSample.Refresh();
        LoadBlobRleSample.Refresh();
        LoadCurvilinearSample.Refresh();
        LoadFewShotSample.Refresh();
        LoadPatchAnomalySample.Refresh();
        LoadCalibrationSample.Refresh();
        LoadCornerAlignmentSample.Refresh();
        LoadIndustrialOcrOcvSample.Refresh();
        LoadLightweightOcrSample.Refresh();
        LoadAutoTeachingSample.Refresh();
        SelectMetrologyCategory.Refresh();
        SelectSurface3DCategory.Refresh();
        SelectCodeOcrCategory.Refresh();
        SelectAiAutoTeachingCategory.Refresh();
        SelectPreprocessingCategory.Refresh();
        SelectAllCategories.Refresh();
        foreach (var option in Algorithms)
        {
            option.SelectCommand?.Refresh();
        }
        Cancel.Refresh();
        Locate.Refresh();
    }

    public void Dispose()
    {
        _disposed = true;
        CancelLivePreview();
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = null;
    }
}
