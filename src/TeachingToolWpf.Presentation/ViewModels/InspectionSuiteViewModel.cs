using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TeachingToolWpf.Commands;
using TeachingToolWpf.Imaging.Matching;
using TeachingToolWpf.Infrastructure;
using TeachingToolWpf.Models;
using VisionAlgorithm.OpenSrc.IndustrialOcrOcv;
using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Geometry;
using VisionCanvas.Rendering;
using VisionGeometry;
using VisionImaging.Abstractions;
using VisionOcr.PpOcr;

namespace TeachingToolWpf.ViewModels;

/// <summary>
/// 상용 정밀 검사 스위트 7종 통합 뷰모델.
/// 선택된 ROI 또는 문서 전체 ROI에 대해 7종 알고리즘을 실행하고 결과를 캔버스 오버레이로 시각화하며,
/// ROI 이동·회전·파라미터 조절 시 실시간 캘리퍼 가이드 및 자동 계측 프리뷰를 제공한다.
/// </summary>
public sealed class InspectionSuiteViewModel : ObservableObject, IVisionAlgorithmPreview
{
    private static readonly Vector4 PassColor = new(0.06f, 0.72f, 0.51f, 1.0f);
    private static readonly Vector4 ReviewColor = new(0.96f, 0.62f, 0.04f, 1.0f);
    private static readonly Vector4 FailColor = new(0.94f, 0.27f, 0.27f, 1.0f);
    private static readonly Vector4 GuideBoundsColor = new(0.22f, 0.74f, 0.97f, 0.88f);
    private static readonly Vector4 GuideInnerColor = new(0.38f, 0.80f, 0.98f, 0.78f);
    private static readonly Vector4 PrimaryFeatureColor = new(0.0f, 0.90f, 1.0f, 1.0f);
    private static readonly Vector4 SecondaryFeatureColor = new(0.98f, 0.80f, 0.08f, 1.0f);
    private static readonly Vector4 DefectFeatureColor = new(1.0f, 0.23f, 0.19f, 1.0f);

    private readonly IInspectionSuiteService _service;
    private readonly ITaughtFontOcvService? _ocvService;
    private readonly Func<string?>? _selectOcvFontOpenPath;
    private readonly Func<string?>? _selectOcvFontSavePath;
    private readonly Func<string, string, float, bool>? _confirmApplyPpOcrToOcv;
    private readonly List<InspectionSuiteRoiInput> _referenceRois = [];
    private readonly Dictionary<Guid, RoiSuiteExecutionMode> _roiExecutionModes = [];
    private readonly object _previewPumpGate = new();
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
    private const float DefaultOcvForegroundContrast = 14.0f;
    private const float MinOcvForegroundContrast = 2.0f;
    private const float MaxOcvForegroundContrast = 80.0f;

    private float _cornerApexIgnoreMargin = 36.0f;
    private float _cornerRansacThresholdPixels = 0.55f;
    private float _cornerOrthogonalityToleranceDeg = 5.0f;
    private OcrAllowedCharacterSet _ocrAllowedCharacters = InspectionSuiteParameters.DefaultOcrAllowedCharacters;
    private PpOcrSpeedPreset _ppOcrPreset = InspectionSuiteParameters.DefaultPpOcrPreset;
    private float _ppOcrMinConfidence = InspectionSuiteParameters.DefaultPpOcrMinConfidence;
    private PpOcrLanguage _ppOcrLanguageMode = InspectionSuiteParameters.DefaultPpOcrLanguageMode;
    private PpOcrPolarityMode _ppOcrPolarity = InspectionSuiteParameters.DefaultPpOcrPolarity;
    private PpOcrPipelineMode _ppOcrPipeline = InspectionSuiteParameters.DefaultPpOcrPipeline;
    private OcvPolaritySelection _selectedOcvPolarity = OcvPolaritySelection.Auto;
    private float _ocvForegroundContrast = DefaultOcvForegroundContrast;
    private bool _ocvAppendTeachMode = true;
    private string _selectedRoiOcvExpectedText = string.Empty;
    private string _selectedRoiOcvFormatMask = string.Empty;
    private string _selectedRoiOcvFontId = RoiOcvSettings.DefaultFontId;
    private bool _syncingRoiOcvFields;
    private OcvRoiInspectionItem? _selectedOcvVerificationItem;
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
    private float _qrCodeMinIsoContrast = 0.20f;
    private int _qrCodeMaxEccErrors = 8;
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
    private bool _isInteractiveEditing;
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
    private InspectionSuiteRoiInput[] _livePreviewRois = [];
    private Guid? _lastOcrPrimaryRoiId;
    private InspectionSuiteExecutionOutput? _lastOcrRenderedOutput;
    private ImmutableArray<byte> _lastEnhancedGrayPixels;
    private int _lastEnhancedPreviewW;
    private int _lastEnhancedPreviewH;
    private ImmutableArray<byte> _lastBinarizedRgbaPixels;
    private int _lastBinarizedPreviewW;
    private int _lastBinarizedPreviewH;
    private bool _isHelpManualOpen;
    private RelayCommand? _toggleHelpManualCommand;

    public bool IsHelpManualOpen
    {
        get => _isHelpManualOpen;
        set => Set(ref _isHelpManualOpen, value);
    }

    public RelayCommand ToggleHelpManual => _toggleHelpManualCommand ??= new(() => IsHelpManualOpen = !IsHelpManualOpen);

    public InspectionAlgorithmRegistry Registry => _service.Registry;

    public AlgorithmHelpManual ActiveHelpManual => AlgorithmHelpCatalog.ForInspectionSuite(_selectedAlgorithm, _service.Registry);

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

    public InspectionSuiteViewModel(
        IInspectionSuiteService service,
        ITaughtFontOcvService? ocvService = null,
        Func<string?>? selectOcvFontOpenPath = null,
        Func<string?>? selectOcvFontSavePath = null,
        Func<string, string, float, bool>? confirmApplyPpOcrToOcv = null)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
        _ocvService = ocvService;
        _selectOcvFontOpenPath = selectOcvFontOpenPath;
        _selectOcvFontSavePath = selectOcvFontSavePath;
        _confirmApplyPpOcrToOcv = confirmApplyPpOcrToOcv;

        Algorithms = _service.Registry.All
            .Select(alg => new InspectionSuiteAlgorithmOption(alg.Kind, alg.ShortName, alg.Category, alg.ShortPillLabel)
            {
                SelectCommand = new RelayCommand(() => SelectedAlgorithm = alg.Kind, () => IsIdle)
            })
            .ToList();

        SelectMetrologyCategory = new RelayCommand(() => SelectCategoryAndDefaultAlgorithm(InspectionSuiteCategory.MetrologyAndGeometry), () => IsIdle);
        SelectSurface3DCategory = new RelayCommand(() => SelectCategoryAndDefaultAlgorithm(InspectionSuiteCategory.SurfaceBlobAnd3D), () => IsIdle);
        SelectOcrCategory = new RelayCommand(() => SelectCategoryAndDefaultAlgorithm(InspectionSuiteCategory.Ocr), () => IsIdle);
        SelectBarcodeCategory = new RelayCommand(() => SelectCategoryAndDefaultAlgorithm(InspectionSuiteCategory.Barcode1D2D), () => IsIdle);
        SelectCodeOcrCategory = SelectOcrCategory;
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

        AllowedCharacterSetOptions =
        [
            new(OcrAllowedCharacterSet.All, "전체 허용 (영문 대소문자·숫자·기호·한글)"),
            new(OcrAllowedCharacterSet.UpperAlphaNumericAndSymbols, "대문자 + 숫자 + 기호 (A-Z, 0-9, / - : .)"),
            new(OcrAllowedCharacterSet.UpperAlphaNumeric, "대문자 + 숫자 (A-Z, 0-9)"),
            new(OcrAllowedCharacterSet.DigitsAndSeparators, "숫자 + 날짜 구분자 (0-9, / - : .)"),
            new(OcrAllowedCharacterSet.DigitsOnly, "숫자 전용 (0-9)"),
            new(OcrAllowedCharacterSet.UpperAlphaOnly, "영문 대문자 전용 (A-Z)")
        ];

        OcvPolarityOptions =
        [
            new(OcvPolaritySelection.Auto, "자동 판별 (배경·글자 명암 자동)"),
            new(OcvPolaritySelection.DarkOnLight, "밝은 배경 위 어두운 글자 (Dark-on-Light)"),
            new(OcvPolaritySelection.LightOnDark, "어두운 배경 위 밝은 글자 (Light-on-Dark)")
        ];

        PpOcrSpeedPresetOptions =
        [
            new(PpOcrSpeedPreset.UniversalAuto, "범용 자동 모드 (다국어 단락회로 · 양극성 · DBNet 자동)"),
            new(PpOcrSpeedPreset.HighSpeedProduction, "고속 양산 모드 (RecOnly · 단일 패스 · 멀티 ROI 배치)")
        ];

        PpOcrLanguageOptions =
        [
            new(PpOcrLanguage.MultilingualConfidenceSelect, "다국어 자동 선택 (영문·한글 최고 신뢰도)"),
            new(PpOcrLanguage.English, "영문·숫자·기호 전용 (en_PP-OCRv4)"),
            new(PpOcrLanguage.Korean, "한글·영문·숫자 (korean_PP-OCRv4)")
        ];

        PpOcrPolarityOptions =
        [
            new(PpOcrPolarityMode.Auto, "자동 양극성 (원본 + 반전 최고 신뢰도)"),
            new(PpOcrPolarityMode.NormalOnly, "원본 극성만 (어두운 글자 우선)"),
            new(PpOcrPolarityMode.InvertedOnly, "명암 반전 (어두운 배경 위 밝은 각인)")
        ];

        PpOcrPipelineOptions =
        [
            new(PpOcrPipelineMode.Auto, "자동 (단일 줄 직접 인식 → 다중 줄 DBNet 검출)"),
            new(PpOcrPipelineMode.RecOnly, "단일 줄 직접 인식 (Rec Only · 고속)"),
            new(PpOcrPipelineMode.DetAndRec, "영역 검출 후 인식 (DBNet Det + Rec · 기울임/다중 줄)")
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
        LoadOcrOcvSample = new(() => RequestSampleLoad(InspectionSuiteAlgorithmKind.PpOcrOnnx), () => IsIdle);
        LoadAutoTeachingSample = new(() => RequestSampleLoad(InspectionSuiteAlgorithmKind.AutoTeaching), () => IsIdle);
        ApplyPpOcrTextToOcvExpectedText = new(CopyRecognizedPpOcrTextToOcvExpectedText,
            () => IsIdle && CanEditSelectedRoiOcvSettings && TryGetSelectedVerifiedPpOcrText(out _, out _, out _));
        TeachSelectedRoiOcvFont = new(TeachSelectedRoiOcvFontAsync, error => PresentWarning(error.Message),
            () => IsIdle && _canExecute && _ocvService is not null && _image is not null && CanEditSelectedRoiOcvSettings && !string.IsNullOrWhiteSpace(_selectedRoiOcvExpectedText));
        VerifySelectedRoiOcv = new(() => VerifyRoisOcvAsync(selectedOnly: true), error => PresentWarning(error.Message),
            () => IsIdle && _canExecute && _ocvService is { HasGoldenFont: true } && _image is not null && (HasSelectedLeafRoi() || HasAnyLeafRoi()));
        VerifyAllRoisOcv = new(() => VerifyRoisOcvAsync(selectedOnly: false), error => PresentWarning(error.Message),
            () => IsIdle && _canExecute && _ocvService is { HasGoldenFont: true } && _image is not null && HasAnyLeafRoi());
        SaveOcvFontModel = new(SaveOcvFontModelFromDialog,
            () => IsIdle && _ocvService is not null && _ocvService.HasGoldenFontForId(_selectedRoiOcvFontId) && _selectOcvFontSavePath is not null);
        LoadOcvFontModel = new(LoadOcvFontModelFromDialog,
            () => IsIdle && _ocvService is not null && _selectOcvFontOpenPath is not null);
        ClearOcvFontModel = new(ClearOcvFontModelState,
            () => IsIdle && _ocvService is { HasGoldenFont: true });
        Cancel = new(() => _cancellation?.Cancel(), () => IsBusy);
        Locate = new(() => LocateRequested?.Invoke(BoundsD.FromPoints((Overlay ?? _overlay)!.Matches.SelectMany(marker => marker.Bounds))),
            () => IsIdle && _canExecute && (Overlay ?? _overlay)?.Matches.IsEmpty == false);
    }

    public event Action? Changed;
    public event Action<BoundsD>? LocateRequested;
    public event Action<InspectionSuiteAlgorithmKind>? SampleLoadRequested;
    public event Action<RoiAlgorithmAssignment>? RoiAssignmentRecorded;
    public event Action<StructuredInspectionLogEntry>? StructuredLogRecorded;
    public event Action<IReadOnlyList<Guid>, RoiOcvSettings>? RoiOcvSettingsChangeRequested;

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
    public bool IsOcrCategorySelected => _selectedCategory == InspectionSuiteCategory.Ocr;
    public bool IsBarcodeCategorySelected => _selectedCategory == InspectionSuiteCategory.Barcode1D2D;
    public bool IsCodeOcrCategorySelected => _selectedCategory == InspectionSuiteCategory.Ocr;
    public bool IsAiAutoTeachingCategorySelected => _selectedCategory == InspectionSuiteCategory.AiAndAutoTeaching;
    public bool IsPreprocessingCategorySelected => _selectedCategory == InspectionSuiteCategory.PreprocessingAndFrequency;
    public bool IsAllCategoriesSelected => _selectedCategory == InspectionSuiteCategory.All;
    public bool IsAnyOcrSelected => _selectedAlgorithm == InspectionSuiteAlgorithmKind.PpOcrOnnx;
    public bool IsPpOcrOnnxSelected => _selectedAlgorithm == InspectionSuiteAlgorithmKind.PpOcrOnnx;
    public bool IsPpOcrWired => _service.IsPpOcrWired;
    public string PpOcrStatusBadge => _service.PpOcrStatusSummary;

    public string ActiveCategoryTitle => _selectedCategory switch
    {
        InspectionSuiteCategory.MetrologyAndGeometry => "치수·기하계측 카테고리 (5종: 1D 캘리퍼·직선·원 / 복합캘리퍼·타원 / 2-Rake 코너 / 다중형상 GD&T / 9-Point 캘리브레이션)",
        InspectionSuiteCategory.SurfaceBlobAnd3D => "블롭·표면·3D 카테고리 (6종: RLE 블롭 / 워터셰드 분리 / Steger 곡선비드 / 골든 마스터 / PaDiM 패치 / 포토메트릭 3D)",
        InspectionSuiteCategory.Ocr => "OCR / OCV 카테고리 (1종 통합: PP-OCRv4 범용 판독 + 티칭 기반 OCV 인쇄 품질 검증)",
        InspectionSuiteCategory.Barcode1D2D => "1D·2D 바코드 카테고리 (3종: 1D Code 128 멀티스캔 리더 / 2D DataMatrix ECC 200 리더 / 2D QR 코드 리더)",
        InspectionSuiteCategory.AiAndAutoTeaching => "AI·자동티칭 카테고리 (2종: 멀티파츠 자동 티칭 / Few-Shot 이상 분류)",
        InspectionSuiteCategory.PreprocessingAndFrequency => "조명·주파수 전처리 카테고리 (2종: 2D 다항식 조명·비등방성 확산·재구성 / 2D FFT 노치·호모모픽·Gabor)",
        _ => $"전체 검사 알고리즘 보기 ({Algorithms.Count}종 전체 목록)"
    };

    public RelayCommand SelectMetrologyCategory { get; }
    public RelayCommand SelectSurface3DCategory { get; }
    public RelayCommand SelectOcrCategory { get; }
    public RelayCommand SelectBarcodeCategory { get; }
    public RelayCommand SelectCodeOcrCategory { get; }
    public RelayCommand SelectAiAutoTeachingCategory { get; }
    public RelayCommand SelectPreprocessingCategory { get; }
    public RelayCommand SelectAllCategories { get; }
    public IReadOnlyList<MetrologyModeOption> MetrologyModes { get; }
    public IReadOnlyList<MetrologyPolarityOption> PolarityModes { get; }
    public IReadOnlyList<OcrAllowedCharacterOption> AllowedCharacterSetOptions { get; }
    public IReadOnlyList<OcvPolarityOption> OcvPolarityOptions { get; }
    public IReadOnlyList<PpOcrSpeedPresetOption> PpOcrSpeedPresetOptions { get; }
    public IReadOnlyList<PpOcrLanguageOption> PpOcrLanguageOptions { get; }
    public IReadOnlyList<PpOcrPolarityOption> PpOcrPolarityOptions { get; }
    public IReadOnlyList<PpOcrPipelineOption> PpOcrPipelineOptions { get; }
    public ObservableCollection<SuiteRegisteredReferenceItem> RegisteredReferenceItems { get; } = [];
    public ObservableCollection<OcrLiveRoiSummaryItem> OcrLiveRoiItems { get; } = [];
    public ObservableCollection<OcvRoiInspectionItem> OcvVerificationItems { get; } = [];
    public bool IsOcvServiceWired => _ocvService is not null;
    public bool HasGoldenOcvFont => _ocvService?.HasGoldenFont == true;
    public bool CanEditSelectedRoiOcvSettings => GetSelectedLeafRois().Count == 1;
    public string OcvFontStatusBadge => _ocvService?.GetRegisteredCharactersSummary(_selectedRoiOcvFontId)
        ?? "골든 폰트 미등록 (단일 기준 ROI 선택 후 '폰트 티칭' 실행)";
    public string OcvLastVerdictBadge { get; private set; } = "READY";
    public string OcvLastVerdictAccent { get; private set; } = "#38BDF8";
    public bool HasOcvVerificationItems => OcvVerificationItems.Count > 0;
    public OcvRoiInspectionItem? SelectedOcvVerificationItem
    {
        get => _selectedOcvVerificationItem;
        set
        {
            if (Set(ref _selectedOcvVerificationItem, value))
            {
                Notify(nameof(SelectedOcvCharacterItems));
                Notify(nameof(HasSelectedOcvCharacterItems));
            }
        }
    }
    public ImmutableArray<OcvCharacterInspectionItem> SelectedOcvCharacterItems =>
        _selectedOcvVerificationItem?.Characters is { IsDefault: false } chars ? chars : ImmutableArray<OcvCharacterInspectionItem>.Empty;
    public bool HasSelectedOcvCharacterItems => !SelectedOcvCharacterItems.IsEmpty;
    public RelayCommand ApplyPpOcrTextToOcvExpectedText { get; }
    public AsyncCommand TeachSelectedRoiOcvFont { get; }
    public AsyncCommand VerifySelectedRoiOcv { get; }
    public AsyncCommand VerifyAllRoisOcv { get; }
    public RelayCommand SaveOcvFontModel { get; }
    public RelayCommand LoadOcvFontModel { get; }
    public RelayCommand ClearOcvFontModel { get; }
    public ImageSource? OcrEnhancedPreviewImage { get; private set; }
    public ImageSource? OcrBinarizedPreviewImage { get; private set; }
    public bool HasOcrLivePreviewImages => OcrEnhancedPreviewImage is not null && OcrBinarizedPreviewImage is not null;
    public string OcrLiveRecognizedText { get; private set; } = "ROI 선택 또는 샘플 열기 대기 중";
    public string OcrLiveVerdictBadge { get; private set; } = "READY";
    public string OcrLiveVerdictAccent { get; private set; } = "#38BDF8";
    public string OcrLiveConfidenceBadge { get; private set; } = "신뢰도 —";
    public double OcrLiveConfidencePercent { get; private set; }
    public string OcrLivePolarityAndCharSummary { get; private set; } = "옵션 조절 시 정류 패치·이진화 진단·문자 박스가 실시간 갱신됩니다.";
    public string OcrLivePreprocessingBadge { get; private set; } = "PP-OCRv4 ONNX + 티칭 OCV 실시간 진단";
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
    public RelayCommand LoadOcrOcvSample { get; }
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
    public bool IsAutoTeachingSelected => _selectedAlgorithm == InspectionSuiteAlgorithmKind.AutoTeaching;
    public bool IsPreprocessingShadingSelected => _selectedAlgorithm == InspectionSuiteAlgorithmKind.PreprocessingShadingAndRecon;
    public bool IsAdvancedMetrologyEllipseSelected => _selectedAlgorithm == InspectionSuiteAlgorithmKind.AdvancedMetrologyEllipse;
    public bool IsAdvancedBlobWatershedSelected => _selectedAlgorithm == InspectionSuiteAlgorithmKind.AdvancedBlobWatershed;
    public bool IsDataMatrix2DSelected => _selectedAlgorithm == InspectionSuiteAlgorithmKind.DataMatrix2DReader;
    public bool IsQrCode2DSelected => _selectedAlgorithm == InspectionSuiteAlgorithmKind.QrCode2DReader;
    public bool IsPreprocessingFftGaborSelected => _selectedAlgorithm == InspectionSuiteAlgorithmKind.PreprocessingFftAndGabor;
    public bool IsBarcode1DSelected => _selectedAlgorithm == InspectionSuiteAlgorithmKind.Barcode1DReader;
    public bool IsPhotometricStereoSelected => _selectedAlgorithm == InspectionSuiteAlgorithmKind.PhotometricStereo;
    public bool IsGeometricTolerancingSelected => _selectedAlgorithm == InspectionSuiteAlgorithmKind.GeometricTolerancing;
    public bool UsesMetrologyOrCornerParams => _selectedAlgorithm is InspectionSuiteAlgorithmKind.Metrology
        or InspectionSuiteAlgorithmKind.CornerAlignment;
    public bool UsesReferenceRois => _service.Registry.GetRequired(_selectedAlgorithm).UsesReferenceRois;
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
            var normalized = _service.Registry.NormalizeKind(value);
            if (Set(ref _selectedAlgorithm, normalized))
            {
                var mappedCategory = _service.Registry.ResolveCategory(normalized);
                if (_selectedCategory != InspectionSuiteCategory.All && _selectedCategory != mappedCategory)
                {
                    _selectedCategory = mappedCategory;
                    NotifyCategoryFlags();
                }

                _overlay = null;
                _livePreview = null;
                _livePreviewRois = [];
                NotifyAlgorithmSelectionChanged();
                UpdateGeometricGuideAndSchedulePreview();
                RefreshCommands();
            }
        }
    }

    public void SelectCategoryAndDefaultAlgorithm(InspectionSuiteCategory category)
    {
        SelectedCategory = category;
        if (category == InspectionSuiteCategory.All)
        {
            return;
        }

        if (_service.Registry.ResolveCategory(_selectedAlgorithm) != category &&
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
        Notify(nameof(IsOcrCategorySelected));
        Notify(nameof(IsBarcodeCategorySelected));
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

    public string AlgorithmDescription => _service.Registry.GetRequired(_selectedAlgorithm).Description;

    public string AlgorithmUsageGuide => _service.Registry.GetRequired(_selectedAlgorithm).GetUsageGuide(BuildParameters());

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

    public string SelectedAlgorithmHeaderTitle => $"3 · {_service.Registry.GetShortName(_selectedAlgorithm)} — 전용 티칭 파라미터";

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

    public OcrAllowedCharacterSet OcrAllowedCharacters
    {
        get => _ocrAllowedCharacters;
        set
        {
            if (Set(ref _ocrAllowedCharacters, value))
            {
                NotifyOptionValidationChanged();
            }
        }
    }

    public PpOcrSpeedPreset PpOcrPreset
    {
        get => _ppOcrPreset;
        set
        {
            if (Set(ref _ppOcrPreset, value))
            {
                NotifyOptionValidationChanged();
            }
        }
    }

    public float PpOcrMinConfidence
    {
        get => _ppOcrMinConfidence;
        set
        {
            float clamped = Math.Clamp(value, 0.05f, 1.0f);
            if (Set(ref _ppOcrMinConfidence, clamped))
            {
                NotifyOptionValidationChanged();
            }
        }
    }

    public PpOcrLanguage PpOcrLanguageMode
    {
        get => _ppOcrLanguageMode;
        set
        {
            if (Set(ref _ppOcrLanguageMode, value))
            {
                NotifyOptionValidationChanged();
            }
        }
    }

    public PpOcrPolarityMode PpOcrPolarity
    {
        get => _ppOcrPolarity;
        set
        {
            if (Set(ref _ppOcrPolarity, value))
            {
                NotifyOptionValidationChanged();
            }
        }
    }

    public PpOcrPipelineMode PpOcrPipeline
    {
        get => _ppOcrPipeline;
        set
        {
            if (Set(ref _ppOcrPipeline, value))
            {
                NotifyOptionValidationChanged();
            }
        }
    }

    public OcvPolaritySelection SelectedOcvPolarity
    {
        get => _selectedOcvPolarity;
        set => Set(ref _selectedOcvPolarity, value);
    }

    public float OcvForegroundContrast
    {
        get => _ocvForegroundContrast;
        set
        {
            float clamped = Math.Clamp(value, MinOcvForegroundContrast, MaxOcvForegroundContrast);
            Set(ref _ocvForegroundContrast, clamped);
        }
    }

    public bool OcvAppendTeachMode
    {
        get => _ocvAppendTeachMode;
        set => Set(ref _ocvAppendTeachMode, value);
    }

    public string SelectedRoiOcvExpectedText
    {
        get => _selectedRoiOcvExpectedText;
        set
        {
            if (!_syncingRoiOcvFields && !CanEditSelectedRoiOcvSettings)
            {
                return;
            }

            string normalized = value ?? string.Empty;
            if (Set(ref _selectedRoiOcvExpectedText, normalized))
            {
                if (!_syncingRoiOcvFields)
                {
                    CommitSelectedRoiOcvSettings();
                }

                TeachSelectedRoiOcvFont.Refresh();
                VerifySelectedRoiOcv.Refresh();
                VerifyAllRoisOcv.Refresh();
            }
        }
    }

    public string SelectedRoiOcvFormatMask
    {
        get => _selectedRoiOcvFormatMask;
        set
        {
            if (!_syncingRoiOcvFields && !CanEditSelectedRoiOcvSettings)
            {
                return;
            }

            string normalized = value ?? string.Empty;
            if (Set(ref _selectedRoiOcvFormatMask, normalized))
            {
                if (!_syncingRoiOcvFields)
                {
                    CommitSelectedRoiOcvSettings();
                }
            }
        }
    }

    public string SelectedRoiOcvFontId
    {
        get => _selectedRoiOcvFontId;
        set
        {
            if (!_syncingRoiOcvFields && !CanEditSelectedRoiOcvSettings)
            {
                return;
            }

            string normalized = RoiOcvSettings.NormalizeFontId(value);
            if (Set(ref _selectedRoiOcvFontId, normalized))
            {
                if (!_syncingRoiOcvFields)
                {
                    CommitSelectedRoiOcvSettings();
                }

                Notify(nameof(OcvFontStatusBadge));
                SaveOcvFontModel.Refresh();
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

    public float QrCodeMinIsoContrast
    {
        get => _qrCodeMinIsoContrast;
        set
        {
            float clamped = Math.Clamp(value, QrCodeOptions.UiMinIsoContrastBound, (float)QrCodeOptions.MaxIsoContrastBound);
            if (Set(ref _qrCodeMinIsoContrast, clamped))
            {
                NotifyOptionValidationChanged();
            }
        }
    }

    public int QrCodeMaxEccErrors
    {
        get => _qrCodeMaxEccErrors;
        set
        {
            int clamped = Math.Clamp(value, QrCodeOptions.MinEccErrorsBound, QrCodeOptions.UiMaxEccErrorsBound);
            if (Set(ref _qrCodeMaxEccErrors, clamped))
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

    public string ParameterPreviewSummary
    {
        get
        {
            var parameters = BuildParameters();
            var algorithm = _service.Registry.GetRequired(_selectedAlgorithm);
            string summary = algorithm.FormatParameterSummary(parameters);
            return algorithm.Kind == InspectionSuiteAlgorithmKind.PpOcrOnnx
                ? $"{summary} · OCV 대비={_ocvForegroundContrast:F1}GV"
                : summary;
        }
    }

    public string OptionsError
    {
        get
        {
            if (_hasInputErrors)
            {
                return "숫자 형식이 올바르지 않은 입력 항목이 있습니다.";
            }

            var parameters = BuildParameters();
            string metrologyError = parameters.Metrology.ValidateForUi();
            if (metrologyError.Length > 0)
            {
                return metrologyError;
            }

            string blobError = parameters.Blob.ValidateForUi();
            if (blobError.Length > 0)
            {
                return blobError;
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

        Update(update, triggerAsyncMeasurement: SynchronizationContext.Current is not null);
    }

    internal void UpdateForTest(InspectionUpdate update, bool triggerAsyncMeasurement)
    {
        if (_disposed)
        {
            return;
        }

        Update(update, triggerAsyncMeasurement);
    }

    private void Update(InspectionUpdate update, bool triggerAsyncMeasurement)
    {
        var state = update.State;
        bool imageChanged = _generation != update.Generation || !Equals(_image, state.Image);
        bool roisChanged = !ReferenceEquals(_lastRoisRef, state.Rois);
        bool selectionChanged = !ReferenceEquals(_lastSelectionRef, state.Selection);
        bool transformChanged = _transformPreview != update.TransformPreview;
        bool interactiveEditActive = update.TransformPreview is not null || (roisChanged && !update.CanExecute);
        bool wasInteractiveEditActive = _isInteractiveEditing;
        bool roisOrSelectionChanged = roisChanged || selectionChanged || transformChanged || (wasInteractiveEditActive != interactiveEditActive);
        bool toolBecameAvailable = !_toolAvailable && update.ToolAvailable;
        bool wasCanExecute = _canExecute;

        _state = state;
        _lastRoisRef = state.Rois;
        _lastSelectionRef = state.Selection;
        _transformPreview = update.TransformPreview;
        _isInteractiveEditing = interactiveEditActive;
        _image = state.Image;
        _generation = update.Generation;
        _live = update.Live;
        _toolAvailable = update.ToolAvailable;
        _canExecute = update.CanExecute && update.ToolAvailable;

        if (imageChanged || roisChanged || selectionChanged)
        {
            SyncSelectedRoiOcvSettingsFromState();
        }

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
            _roiExecutionModes.Clear();
            RegisteredReferenceItems.Clear();
            OcvVerificationItems.Clear();
            SelectedOcvVerificationItem = null;
            OcvLastVerdictBadge = "READY";
            OcvLastVerdictAccent = "#38BDF8";
            Notify(nameof(HasOcvVerificationItems));
            Notify(nameof(OcvLastVerdictBadge));
            Notify(nameof(OcvLastVerdictAccent));
            _overlay = null;
            _livePreview = null;
            _livePreviewRois = [];
            LastOutput = null;
            UpdateOcrDiagnosticViewState(null);
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
                UpdateGeometricGuideAndSchedulePreview(triggerAsyncMeasurement);
            }
            else
            {
                Notify(nameof(Overlay));
                Changed?.Invoke();
            }
        }
        else if (roisOrSelectionChanged || toolBecameAvailable)
        {
            if (_overlay is not null && (roisChanged || transformChanged))
            {
                // ROI 편집(드래그/크기/회전/키보드 이동) 시작 시 기존 결과를 빈 가이드로 지우지 않고
                // 실시간 프리뷰 오버레이로 승격해 0ms 포즈 추적과 백그라운드 재추론으로 깜빡임을 원천 차단한다.
                _livePreview = _overlay;
                _overlay = null;
            }
            if ((selectionChanged || toolBecameAvailable) && IsAnyOcrSelected && LastOutput is { } lastOut && lastOut.Algorithm == _selectedAlgorithm)
            {
                UpdateOcrDiagnosticViewState(lastOut);
            }
            UpdateGeometricGuideAndSchedulePreview(triggerAsyncMeasurement);
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
        _livePreviewRois = [];
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
        using var cts = BeginBusy($"{_service.Registry.GetShortName(algorithm)} 실행 중… (ROI {targets.Length}개)");
        try
        {
            var output = await _service.ExecuteRoiInspectionAsync(image, _live, algorithm, targets, refs, parameters, cts.Token);
            if (epoch != _epoch || _disposed)
            {
                return;
            }

            ApplyExecutionOutput(output, targets);
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

    private void ApplyExecutionOutput(
        InspectionSuiteExecutionOutput output,
        InspectionSuiteRoiInput[]? executionTargets = null)
    {
        LastOutput = output;
        _overlay = BuildResultOverlayFromOutput(output, isLivePreview: false);
        _livePreviewRois = executionTargets ?? BuildTargetSnapshotsFromOutput(output);

        Status = output.SummaryTitle;
        Results = output.DetailedReport;
        UpdateOcrDiagnosticViewState(output);
        EmitSuiteExecutionResults(output.Items, output.Algorithm, output.TotalMilliseconds);
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
                SourceRoiId = item.RoiId,
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
                _livePreviewRois = [];
                Notify(nameof(Overlay));
                Changed?.Invoke();
            }
            return;
        }

        var parameters = BuildParameters() with { IsInteractivePreview = _isInteractiveEditing };
        var targets = previewRois.Select(roi => ToRoiInput(roi, classLabel: 0)).ToArray();
        bool canScheduleAsyncMeasurement = triggerAsyncMeasurement && _autoPreview && _showOverlay && IsIdle && _image is not null && OptionsError.Length == 0;

        // 1. 이미 실시간 프리뷰 결과가 존재하는 상태에서 ROI를 이동/회전/크기 조절할 때는
        //    빈 청록색 가이드 미리보기로 덮어써서 발생하는 스트로브(깜빡임)를 원천 차단하고,
        //    기존 마커·윤곽·특징점을 현재 ROI 포즈로 0ms 동기 변환해 60FPS 추적을 보장한다.
        //    파라미터(예: MetrologyMode, CaliperRakeCount)가 변경되어 포즈 변화는 없지만 가이드 형상이 바뀐 경우에는
        //    기존 마커의 InnerContours만 새 파라미터 기준으로 즉시 동기 갱신한다.
        if (_overlay is null && _livePreview is not null && TryProjectLivePreviewOntoTargets(_livePreview, _livePreviewRois, targets, parameters, out var projectedPreview))
        {
            _livePreview = projectedPreview;
            _livePreviewRois = targets;
            Notify(nameof(Overlay));
            Locate.Refresh();
            Changed?.Invoke();
        }
        else if (_overlay is null && _livePreview is not null && HasMatchingTargetIds(_livePreviewRois, targets) &&
                 !_service.Registry.GetRequired(_selectedAlgorithm).SuppressesGuideOverlayWhenEvaluated &&
                 TryRefreshLivePreviewGuides(_livePreview, targets, parameters, out var refreshedGuideOverlay))
        {
            _livePreview = refreshedGuideOverlay;
            _livePreviewRois = targets;
            Notify(nameof(Overlay));
            Locate.Refresh();
            Changed?.Invoke();
        }
        else if (_overlay is null && (_livePreview is null || !HasMatchingTargetIds(_livePreviewRois, targets) || !canScheduleAsyncMeasurement))
        {
            var guideMarkers = ImmutableArray.CreateBuilder<MatchMarker>(targets.Length);
            foreach (var target in targets)
            {
                var guides = _service.Registry.BuildGeometricGuides(target, _selectedAlgorithm, parameters);
                guideMarkers.Add(new MatchMarker(
                    Bounds: target.Outline,
                    ReferencePoint: target.Center,
                    AngleRadians: target.AngleRadians,
                    Label: $"{target.Name} · 가이드 미리보기",
                    Color: GuideBoundsColor,
                    Features: [])
                {
                    SourceRoiId = target.RoiId,
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
                Summary: $"[가이드 미리보기] {_service.Registry.GetShortName(_selectedAlgorithm)} · {ParameterPreviewSummary}",
                Color: GuideBoundsColor,
                SearchBoundary: [],
                Matches: guideMarkers.MoveToImmutable(),
                ShowFeatures: true);
            _livePreviewRois = targets;
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

    private static InspectionSuiteRoiInput[] BuildTargetSnapshotsFromOutput(InspectionSuiteExecutionOutput output)
    {
        if (output.Items.IsDefaultOrEmpty)
        {
            return [];
        }

        var snapshots = new List<InspectionSuiteRoiInput>(output.Items.Length);
        var seenRoiIds = new HashSet<Guid>();
        for (int i = 0; i < output.Items.Length; i++)
        {
            var item = output.Items[i];
            if (item.RoiId != Guid.Empty && !seenRoiIds.Add(item.RoiId))
            {
                continue;
            }

            BoundsD bounds = item.Bounds.IsDefaultOrEmpty
                ? new BoundsD(item.ReferencePoint.X - 4.0, item.ReferencePoint.Y - 4.0, item.ReferencePoint.X + 4.0, item.ReferencePoint.Y + 4.0)
                : BoundsD.FromPoints(item.Bounds);
            snapshots.Add(new InspectionSuiteRoiInput(
                item.RoiId,
                item.Name,
                item.ReferencePoint,
                Math.Max(8.0, bounds.Width),
                Math.Max(8.0, bounds.Height),
                item.AngleRadians,
                item.Bounds));
        }

        return snapshots.ToArray();
    }

    private bool TryRefreshLivePreviewGuides(
        ResultOverlay existingOverlay,
        InspectionSuiteRoiInput[] targets,
        InspectionSuiteParameters parameters,
        out ResultOverlay refreshedOverlay)
    {
        refreshedOverlay = existingOverlay;
        if (existingOverlay.Matches.IsDefaultOrEmpty)
        {
            return false;
        }

        bool isGuideOverlay = existingOverlay.Summary.StartsWith("[가이드 미리보기]", StringComparison.Ordinal);
        var refreshedMarkers = ImmutableArray.CreateBuilder<MatchMarker>(existingOverlay.Matches.Length);
        var guideAddedRoiIds = new HashSet<Guid>();

        for (int m = 0; m < existingOverlay.Matches.Length; m++)
        {
            var marker = existingOverlay.Matches[m];
            int targetIdx = FindOwningTargetIndex(marker, m, existingOverlay.Matches.Length, targets);
            if (targetIdx < 0)
            {
                return false;
            }

            var target = targets[targetIdx];
            bool attachGuides = isGuideOverlay || target.RoiId == Guid.Empty || guideAddedRoiIds.Add(target.RoiId);
            var guides = attachGuides
                ? _service.Registry.BuildGeometricGuides(target, _selectedAlgorithm, parameters)
                : ImmutableArray<ImmutableArray<PointD>>.Empty;

            refreshedMarkers.Add(marker with
            {
                InnerContours = guides
            });
        }

        refreshedOverlay = existingOverlay with
        {
            Matches = refreshedMarkers.MoveToImmutable(),
            PresentationId = Guid.NewGuid()
        };
        return true;
    }

    private bool TryProjectLivePreviewOntoTargets(
        ResultOverlay existingOverlay,
        InspectionSuiteRoiInput[] previousTargets,
        InspectionSuiteRoiInput[] currentTargets,
        InspectionSuiteParameters parameters,
        out ResultOverlay projectedOverlay)
    {
        projectedOverlay = existingOverlay;
        if (existingOverlay.Matches.IsDefaultOrEmpty || previousTargets.Length != currentTargets.Length)
        {
            return false;
        }

        bool anyPoseChanged = false;
        for (int i = 0; i < currentTargets.Length; i++)
        {
            if (previousTargets[i].RoiId != currentTargets[i].RoiId)
            {
                return false;
            }

            if (HasMeaningfulPoseDelta(previousTargets[i], currentTargets[i]))
            {
                anyPoseChanged = true;
            }
        }

        if (!anyPoseChanged)
        {
            return false;
        }

        bool isGuideOverlay = existingOverlay.Summary.StartsWith("[가이드 미리보기]", StringComparison.Ordinal);
        bool suppressGuide = _service.Registry.GetRequired(_selectedAlgorithm).SuppressesGuideOverlayWhenEvaluated && !isGuideOverlay;
        var updatedMarkers = ImmutableArray.CreateBuilder<MatchMarker>(existingOverlay.Matches.Length);
        var guideAddedRoiIds = new HashSet<Guid>();

        for (int m = 0; m < existingOverlay.Matches.Length; m++)
        {
            var marker = existingOverlay.Matches[m];
            int targetIdx = FindOwningTargetIndex(marker, m, existingOverlay.Matches.Length, currentTargets);
            if (targetIdx < 0)
            {
                return false;
            }

            var prev = previousTargets[targetIdx];
            var curr = currentTargets[targetIdx];

            if (!HasMeaningfulPoseDelta(prev, curr))
            {
                if (!isGuideOverlay && curr.RoiId != Guid.Empty)
                {
                    guideAddedRoiIds.Add(curr.RoiId);
                }

                updatedMarkers.Add(marker);
                continue;
            }

            bool attachGuides = !suppressGuide && (isGuideOverlay || curr.RoiId == Guid.Empty || guideAddedRoiIds.Add(curr.RoiId));
            var updatedGuides = attachGuides
                ? _service.Registry.BuildGeometricGuides(curr, _selectedAlgorithm, parameters)
                : ImmutableArray<ImmutableArray<PointD>>.Empty;

            if (isGuideOverlay)
            {
                updatedMarkers.Add(marker with
                {
                    Bounds = curr.Outline,
                    ReferencePoint = curr.Center,
                    AngleRadians = curr.AngleRadians,
                    InnerContours = updatedGuides
                });
            }
            else
            {
                double deltaAngle = curr.AngleRadians - prev.AngleRadians;
                updatedMarkers.Add(marker with
                {
                    Bounds = TransformPointsRelativeToRoi(marker.Bounds, prev, curr),
                    ReferencePoint = TransformPointRelativeToRoi(marker.ReferencePoint, prev, curr),
                    AngleRadians = marker.AngleRadians + deltaAngle,
                    InnerContours = updatedGuides,
                    AdditionalContours = TransformContoursRelativeToRoi(marker.AdditionalContours, prev, curr),
                    Features = TransformPointsRelativeToRoi(marker.Features, prev, curr),
                    KeyFeatures = TransformPointsRelativeToRoi(marker.KeyFeatures, prev, curr),
                    UnmatchedFeatures = TransformPointsRelativeToRoi(marker.UnmatchedFeatures, prev, curr),
                    DiagnosticGroups = TransformDiagnosticGroupsRelativeToRoi(marker.DiagnosticGroups, prev, curr)
                });
            }
        }

        projectedOverlay = existingOverlay with
        {
            Matches = updatedMarkers.MoveToImmutable(),
            PresentationId = Guid.NewGuid()
        };
        return true;
    }

    private static int FindOwningTargetIndex(
        MatchMarker marker,
        int markerIndex,
        int totalMarkerCount,
        InspectionSuiteRoiInput[] targets)
    {
        if (marker.SourceRoiId != Guid.Empty)
        {
            for (int i = 0; i < targets.Length; i++)
            {
                if (targets[i].RoiId == marker.SourceRoiId)
                {
                    return i;
                }
            }

            return -1;
        }

        return totalMarkerCount == targets.Length && (uint)markerIndex < (uint)targets.Length
            ? markerIndex
            : -1;
    }

    private static PointD TransformPointRelativeToRoi(
        PointD point,
        InspectionSuiteRoiInput previous,
        InspectionSuiteRoiInput current)
    {
        double cosPrev = Math.Cos(previous.AngleRadians);
        double sinPrev = Math.Sin(previous.AngleRadians);
        double cosCurr = Math.Cos(current.AngleRadians);
        double sinCurr = Math.Sin(current.AngleRadians);
        double scaleX = previous.Width > 1.0 ? current.Width / previous.Width : 1.0;
        double scaleY = previous.Height > 1.0 ? current.Height / previous.Height : 1.0;

        double dx = point.X - previous.Center.X;
        double dy = point.Y - previous.Center.Y;
        double localX = ((dx * cosPrev) + (dy * sinPrev)) * scaleX;
        double localY = ((-dx * sinPrev) + (dy * cosPrev)) * scaleY;
        double worldX = current.Center.X + (localX * cosCurr) - (localY * sinCurr);
        double worldY = current.Center.Y + (localX * sinCurr) + (localY * cosCurr);
        return new PointD(worldX, worldY);
    }

    private static bool HasMatchingTargetIds(InspectionSuiteRoiInput[] previousTargets, InspectionSuiteRoiInput[] currentTargets)
    {
        if (previousTargets.Length != currentTargets.Length)
        {
            return false;
        }

        for (int i = 0; i < currentTargets.Length; i++)
        {
            if (previousTargets[i].RoiId != currentTargets[i].RoiId)
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasMeaningfulPoseDelta(InspectionSuiteRoiInput previous, InspectionSuiteRoiInput current)
    {
        const double PositionEpsilonPx = 1e-3;
        const double AngleEpsilonRad = 1e-5;
        return Math.Abs(previous.Center.X - current.Center.X) > PositionEpsilonPx
            || Math.Abs(previous.Center.Y - current.Center.Y) > PositionEpsilonPx
            || Math.Abs(previous.Width - current.Width) > PositionEpsilonPx
            || Math.Abs(previous.Height - current.Height) > PositionEpsilonPx
            || Math.Abs(previous.AngleRadians - current.AngleRadians) > AngleEpsilonRad;
    }

    private static ImmutableArray<ImmutableArray<PointD>> TransformContoursRelativeToRoi(
        ImmutableArray<ImmutableArray<PointD>> contours,
        InspectionSuiteRoiInput previous,
        InspectionSuiteRoiInput current)
    {
        if (contours.IsDefaultOrEmpty)
        {
            return contours;
        }

        var builder = ImmutableArray.CreateBuilder<ImmutableArray<PointD>>(contours.Length);
        for (int i = 0; i < contours.Length; i++)
        {
            builder.Add(TransformPointsRelativeToRoi(contours[i], previous, current));
        }

        return builder.MoveToImmutable();
    }

    private static ImmutableArray<DiagnosticPointGroup> TransformDiagnosticGroupsRelativeToRoi(
        ImmutableArray<DiagnosticPointGroup> groups,
        InspectionSuiteRoiInput previous,
        InspectionSuiteRoiInput current)
    {
        if (groups.IsDefaultOrEmpty)
        {
            return groups;
        }

        var builder = ImmutableArray.CreateBuilder<DiagnosticPointGroup>(groups.Length);
        for (int i = 0; i < groups.Length; i++)
        {
            var g = groups[i];
            builder.Add(new DiagnosticPointGroup(g.Color, TransformPointsRelativeToRoi(g.Points, previous, current), g.HalfSize));
        }

        return builder.MoveToImmutable();
    }

    private static ImmutableArray<PointD> TransformPointsRelativeToRoi(
        ImmutableArray<PointD> points,
        InspectionSuiteRoiInput previous,
        InspectionSuiteRoiInput current)
    {
        if (points.IsDefaultOrEmpty)
        {
            return points;
        }

        double cosPrev = Math.Cos(previous.AngleRadians);
        double sinPrev = Math.Sin(previous.AngleRadians);
        double cosCurr = Math.Cos(current.AngleRadians);
        double sinCurr = Math.Sin(current.AngleRadians);
        double scaleX = previous.Width > 1.0 ? current.Width / previous.Width : 1.0;
        double scaleY = previous.Height > 1.0 ? current.Height / previous.Height : 1.0;

        var builder = ImmutableArray.CreateBuilder<PointD>(points.Length);
        for (int i = 0; i < points.Length; i++)
        {
            double dx = points[i].X - previous.Center.X;
            double dy = points[i].Y - previous.Center.Y;
            double localX = ((dx * cosPrev) + (dy * sinPrev)) * scaleX;
            double localY = ((-dx * sinPrev) + (dy * cosPrev)) * scaleY;
            double worldX = current.Center.X + (localX * cosCurr) - (localY * sinCurr);
            double worldY = current.Center.Y + (localX * sinCurr) + (localY * cosCurr);
            builder.Add(new PointD(worldX, worldY));
        }

        return builder.MoveToImmutable();
    }

    private void ScheduleLivePreview(InspectionSuiteRoiInput[] targets, InspectionSuiteParameters parameters)
    {
        if (_image is not { } image || targets.Length == 0)
        {
            return;
        }

        long epoch = Interlocked.Increment(ref _previewEpoch);
        var refs = _referenceRois.ToArray();
        var request = new LivePreviewRequest(
            image,
            _live,
            _selectedAlgorithm,
            targets,
            refs,
            parameters,
            SynchronizationContext.Current,
            epoch,
            _generation);

        lock (_previewPumpGate)
        {
            Volatile.Write(ref _pendingPreview, request);
            if (_previewPump is null || _previewPump.IsCompleted)
            {
                _previewPump = Task.Run(RunLivePreviewPumpAsync);
            }
        }
    }

    private async Task RunLivePreviewPumpAsync()
    {
        try
        {
            while (!_disposed)
            {
                LivePreviewRequest? request;
                lock (_previewPumpGate)
                {
                    request = Interlocked.Exchange(ref _pendingPreview, null);
                    if (request is null)
                    {
                        _previewPump = null;
                        return;
                    }
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
                PublishLivePreview(request.UiContext, request.Epoch, request.Generation, request.Algorithm, request.Targets, request.Parameters, output, overlay);
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
        finally
        {
            lock (_previewPumpGate)
            {
                if (_disposed || _pendingPreview is null)
                {
                    _previewPump = null;
                }
                else
                {
                    _previewPump = Task.Run(RunLivePreviewPumpAsync);
                }
            }
        }
    }

    private void PublishLivePreview(
        SynchronizationContext? uiContext,
        long previewEpoch,
        long previewGeneration,
        InspectionSuiteAlgorithmKind algorithm,
        InspectionSuiteRoiInput[] requestTargets,
        InspectionSuiteParameters requestParameters,
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
            var currentRois = GetPreviewTargetRois();
            var currentTargets = currentRois.Count > 0
                ? currentRois.Select(roi => ToRoiInput(roi, classLabel: 0)).ToArray()
                : requestTargets;

            if (TryProjectLivePreviewOntoTargets(overlay, requestTargets, currentTargets, requestParameters, out var syncedOverlay))
            {
                _livePreview = syncedOverlay;
                _livePreviewRois = currentTargets;
            }
            else
            {
                _livePreview = overlay;
                _livePreviewRois = requestTargets;
            }

            LastOutput = output;
            Status = $"[실시간 프리뷰] {output.SummaryTitle}";
            Results = output.DetailedReport;
            UpdateOcrDiagnosticViewState(output);
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

    private void UpdateOcrDiagnosticViewState(InspectionSuiteExecutionOutput? output)
    {
        if (output is null || output.Items.IsEmpty || !IsAnyOcrSelected)
        {
            if (OcrLiveRoiItems.Count > 0)
            {
                OcrLiveRoiItems.Clear();
            }

            _lastOcrRenderedOutput = null;
            _lastOcrPrimaryRoiId = null;
            _lastEnhancedGrayPixels = default;
            _lastBinarizedRgbaPixels = default;
            _lastEnhancedPreviewW = 0;
            _lastEnhancedPreviewH = 0;
            _lastBinarizedPreviewW = 0;
            _lastBinarizedPreviewH = 0;

            bool hadImages = HasOcrLivePreviewImages;
            OcrEnhancedPreviewImage = null;
            OcrBinarizedPreviewImage = null;
            string emptyText = _image is null
                ? "상단 '현재 알고리즘 샘플 열기' 또는 OCR 벤치마크(1~7)를 열어 주세요"
                : "ROI 선택 또는 슬라이더 조절 시 실시간 판독 결과가 표시됩니다";
            ApplyOcrLiveScalarState(
                recognizedText: emptyText,
                verdictBadge: "READY",
                verdictAccent: "#38BDF8",
                confidencePercent: 0.0,
                confidenceBadge: "신뢰도 —",
                polaritySummary: "슬라이더 조절 시 전처리·적응 이진화·문자 분할 박스가 실시간 갱신됩니다.",
                preprocessingBadge: ParameterPreviewSummary,
                imagesChanged: hadImages);
            ApplyPpOcrTextToOcvExpectedText.Refresh();
            return;
        }

        var selectedIds = _state.Selection;
        InspectionSuiteItemResult? primaryItem = null;
        foreach (var item in output.Items)
        {
            if (item.OcrDiagnostic is not null && selectedIds.Contains(item.RoiId))
            {
                primaryItem = item;
                break;
            }
        }
        primaryItem ??= output.Items.FirstOrDefault(i => i.OcrDiagnostic is not null) ?? output.Items[0];

        if (ReferenceEquals(_lastOcrRenderedOutput, output) && _lastOcrPrimaryRoiId == primaryItem.RoiId)
        {
            return;
        }

        _lastOcrRenderedOutput = output;
        _lastOcrPrimaryRoiId = primaryItem.RoiId;
        SyncOcrLiveRoiItemsInPlace(output.Items);

        bool bitmapsChanged = false;
        if (primaryItem.OcrDiagnostic is { } primaryDiag)
        {
            if (!ArePixelBuffersEqual(_lastEnhancedPreviewW, _lastEnhancedPreviewH, _lastEnhancedGrayPixels, primaryDiag.PreviewWidth, primaryDiag.PreviewHeight, primaryDiag.EnhancedGrayPixels))
            {
                OcrEnhancedPreviewImage = CreateGrayWriteableBitmap(
                    primaryDiag.PreviewWidth,
                    primaryDiag.PreviewHeight,
                    primaryDiag.EnhancedGrayPixels);
                _lastEnhancedPreviewW = primaryDiag.PreviewWidth;
                _lastEnhancedPreviewH = primaryDiag.PreviewHeight;
                _lastEnhancedGrayPixels = primaryDiag.EnhancedGrayPixels;
                bitmapsChanged = true;
            }

            if (!ArePixelBuffersEqual(_lastBinarizedPreviewW, _lastBinarizedPreviewH, _lastBinarizedRgbaPixels, primaryDiag.PreviewWidth, primaryDiag.PreviewHeight, primaryDiag.BinarizedRgbaPixels))
            {
                OcrBinarizedPreviewImage = CreateBgraWriteableBitmap(
                    primaryDiag.PreviewWidth,
                    primaryDiag.PreviewHeight,
                    primaryDiag.BinarizedRgbaPixels);
                _lastBinarizedPreviewW = primaryDiag.PreviewWidth;
                _lastBinarizedPreviewH = primaryDiag.PreviewHeight;
                _lastBinarizedRgbaPixels = primaryDiag.BinarizedRgbaPixels;
                bitmapsChanged = true;
            }

            string display = string.IsNullOrWhiteSpace(primaryDiag.RecognizedText)
                ? (_selectedAlgorithm == InspectionSuiteAlgorithmKind.PpOcrOnnx
                    ? "(문자를 찾지 못했습니다 — ROI 위치·크기 또는 극성/파이프라인 옵션을 확인해 보세요)"
                    : "(문자를 찾지 못했습니다 — 대비 오프셋·초해상도 슬라이더를 조절해 보세요)")
                : primaryDiag.RecognizedText.Replace("\n", "  /  ", StringComparison.Ordinal);
            string polText = primaryDiag.DarkOnLight ? "밝은 배경 위 어두운 글자 (Dark-on-Light)" : "어두운 배경 위 밝은 글자 (Light-on-Dark)";
            ApplyOcrLiveScalarState(
                recognizedText: display,
                verdictBadge: primaryItem.IsPass ? "PASS" : "REVIEW / NG",
                verdictAccent: primaryItem.IsPass ? "#34D399" : "#F87171",
                confidencePercent: Math.Clamp(primaryDiag.MeanConfidence * 100.0, 0.0, 100.0),
                confidenceBadge: $"평균 {primaryDiag.MeanConfidence * 100.0f:F1}% · 최소 {primaryDiag.MinConfidence * 100.0f:F1}% (기준 ≥{primaryDiag.AcceptThreshold * 100.0f:F0}%)",
                polaritySummary: $"[{primaryItem.Name}] 검출 문자 {primaryDiag.Characters.Length}개 ({primaryDiag.Lines.Length}줄) · 극성: {polText}",
                preprocessingBadge: primaryDiag.PreprocessingBadge,
                imagesChanged: bitmapsChanged);
        }
        else
        {
            bitmapsChanged = HasOcrLivePreviewImages;
            OcrEnhancedPreviewImage = null;
            OcrBinarizedPreviewImage = null;
            _lastEnhancedGrayPixels = default;
            _lastBinarizedRgbaPixels = default;
            _lastEnhancedPreviewW = 0;
            _lastEnhancedPreviewH = 0;
            _lastBinarizedPreviewW = 0;
            _lastBinarizedPreviewH = 0;
            ApplyOcrLiveScalarState(
                recognizedText: primaryItem.BadgeLabel,
                verdictBadge: primaryItem.IsPass ? "PASS" : "NG",
                verdictAccent: primaryItem.IsPass ? "#34D399" : "#F87171",
                confidencePercent: primaryItem.IsPass ? 90.0 : 25.0,
                confidenceBadge: primaryItem.BadgeLabel,
                polaritySummary: primaryItem.DetailLine,
                preprocessingBadge: ParameterPreviewSummary,
                imagesChanged: bitmapsChanged);
        }

        ApplyPpOcrTextToOcvExpectedText.Refresh();
    }

    private void SyncOcrLiveRoiItemsInPlace(ImmutableArray<InspectionSuiteItemResult> items)
    {
        for (int i = 0; i < items.Length; i++)
        {
            var item = items[i];
            var diag = item.OcrDiagnostic;
            string decoded = diag is not null && !string.IsNullOrWhiteSpace(diag.RecognizedText)
                ? diag.RecognizedText.Replace("\n", " / ", StringComparison.Ordinal)
                : item.BadgeLabel;
            string confStr = diag is not null
                ? $"{diag.MeanConfidence * 100.0f:F1}% (최소 {diag.MinConfidence * 100.0f:F0}%)"
                : item.BadgeLabel;
            string charCountStr = diag is not null
                ? $"{diag.Characters.Length}글자 · {diag.Lines.Length}줄"
                : "1줄";

            var nextSummary = new OcrLiveRoiSummaryItem(
                RoiId: item.RoiId,
                RoiName: item.Name,
                DecodedText: string.IsNullOrWhiteSpace(decoded) ? "(미검출)" : decoded,
                ConfidenceBadge: confStr,
                VerdictBadge: item.IsPass ? "PASS" : "NG",
                VerdictAccent: item.IsPass ? "#34D399" : "#F87171",
                CharacterCountBadge: charCountStr);

            if (i < OcrLiveRoiItems.Count)
            {
                if (OcrLiveRoiItems[i] != nextSummary)
                {
                    OcrLiveRoiItems[i] = nextSummary;
                }
            }
            else
            {
                OcrLiveRoiItems.Add(nextSummary);
            }
        }

        while (OcrLiveRoiItems.Count > items.Length)
        {
            OcrLiveRoiItems.RemoveAt(OcrLiveRoiItems.Count - 1);
        }
    }

    private static bool ArePixelBuffersEqual(
        int prevW,
        int prevH,
        ImmutableArray<byte> prevPixels,
        int nextW,
        int nextH,
        ImmutableArray<byte> nextPixels)
    {
        if (prevW != nextW || prevH != nextH || prevPixels.IsDefault || nextPixels.IsDefault || prevPixels.Length != nextPixels.Length)
        {
            return false;
        }

        return prevPixels.AsSpan().SequenceEqual(nextPixels.AsSpan());
    }

    private void ApplyOcrLiveScalarState(
        string recognizedText,
        string verdictBadge,
        string verdictAccent,
        double confidencePercent,
        string confidenceBadge,
        string polaritySummary,
        string preprocessingBadge,
        bool imagesChanged)
    {
        if (imagesChanged)
        {
            Notify(nameof(OcrEnhancedPreviewImage));
            Notify(nameof(OcrBinarizedPreviewImage));
            Notify(nameof(HasOcrLivePreviewImages));
        }

        if (!string.Equals(OcrLiveRecognizedText, recognizedText, StringComparison.Ordinal))
        {
            OcrLiveRecognizedText = recognizedText;
            Notify(nameof(OcrLiveRecognizedText));
        }

        if (!string.Equals(OcrLiveVerdictBadge, verdictBadge, StringComparison.Ordinal))
        {
            OcrLiveVerdictBadge = verdictBadge;
            Notify(nameof(OcrLiveVerdictBadge));
        }

        if (!string.Equals(OcrLiveVerdictAccent, verdictAccent, StringComparison.Ordinal))
        {
            OcrLiveVerdictAccent = verdictAccent;
            Notify(nameof(OcrLiveVerdictAccent));
        }

        if (Math.Abs(OcrLiveConfidencePercent - confidencePercent) > 1e-4)
        {
            OcrLiveConfidencePercent = confidencePercent;
            Notify(nameof(OcrLiveConfidencePercent));
        }

        if (!string.Equals(OcrLiveConfidenceBadge, confidenceBadge, StringComparison.Ordinal))
        {
            OcrLiveConfidenceBadge = confidenceBadge;
            Notify(nameof(OcrLiveConfidenceBadge));
        }

        if (!string.Equals(OcrLivePolarityAndCharSummary, polaritySummary, StringComparison.Ordinal))
        {
            OcrLivePolarityAndCharSummary = polaritySummary;
            Notify(nameof(OcrLivePolarityAndCharSummary));
        }

        if (!string.Equals(OcrLivePreprocessingBadge, preprocessingBadge, StringComparison.Ordinal))
        {
            OcrLivePreprocessingBadge = preprocessingBadge;
            Notify(nameof(OcrLivePreprocessingBadge));
        }
    }

    private static BitmapSource? CreateGrayWriteableBitmap(int width, int height, ImmutableArray<byte> pixels)
    {
        if (width <= 0 || height <= 0 || pixels.IsDefaultOrEmpty || pixels.Length < width * height)
        {
            return null;
        }

        byte[] raw = pixels.ToArray();
        var bmp = BitmapSource.Create(
            width,
            height,
            96.0,
            96.0,
            PixelFormats.Gray8,
            null,
            raw,
            width);
        bmp.Freeze();
        return bmp;
    }

    private static BitmapSource? CreateBgraWriteableBitmap(int width, int height, ImmutableArray<byte> pixels)
    {
        int stride = width * 4;
        if (width <= 0 || height <= 0 || pixels.IsDefaultOrEmpty || pixels.Length < stride * height)
        {
            return null;
        }

        byte[] raw = pixels.ToArray();
        var bmp = BitmapSource.Create(
            width,
            height,
            96.0,
            96.0,
            PixelFormats.Bgra32,
            null,
            raw,
            stride);
        bmp.Freeze();
        return bmp;
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
            AlgorithmBadge: RoiAlgorithmAssignment.FormatAlgorithmBadge(InspectionToolKind.InspectionSuite, null, _selectedAlgorithm, _service.Registry),
            AlgorithmAccent: RoiAlgorithmAssignment.GetAlgorithmAccent(InspectionToolKind.InspectionSuite, null, _selectedAlgorithm),
            ParameterSummary: ParameterPreviewSummary,
            VerdictBadge: isNormal ? "기준:정상" : "기준:불량",
            VerdictAccent: isNormal ? "#34D399" : "#FBBF24",
            LastResultSummary: $"{_service.Registry.GetShortName(_selectedAlgorithm)} 기준 샘플 ({(isNormal ? "정상" : "불량")})",
            SuiteExecutionMode: input.ExecutionMode));
    }

    private void EmitSuiteExecutionResults(
        ImmutableArray<InspectionSuiteItemResult> items,
        InspectionSuiteAlgorithmKind algorithm,
        double totalMilliseconds)
    {
        if (items.IsDefaultOrEmpty)
        {
            return;
        }

        string badge = RoiAlgorithmAssignment.FormatAlgorithmBadge(InspectionToolKind.InspectionSuite, null, algorithm, _service.Registry);
        string accent = RoiAlgorithmAssignment.GetAlgorithmAccent(InspectionToolKind.InspectionSuite, null, algorithm);
        double perItemMs = totalMilliseconds / items.Length;

        var roiOrder = new List<Guid>(items.Length);
        var roiGroups = new Dictionary<Guid, List<InspectionSuiteItemResult>>(items.Length);

        foreach (var item in items)
        {
            if (item.RoiId != Guid.Empty)
            {
                if (!roiGroups.TryGetValue(item.RoiId, out var list))
                {
                    list = [];
                    roiGroups[item.RoiId] = list;
                    roiOrder.Add(item.RoiId);
                }

                list.Add(item);
            }

            var verdict = item.IsPass ? InspectionVerdict.Pass : InspectionVerdict.Fail;
            string verdictBadge = item.IsPass ? "PASS" : "NG";
            string verdictAccent = item.IsPass ? "#34D399" : "#F87171";
            double angleDeg = item.AngleRadians * (180.0 / Math.PI);
            string poseSummary = $"X={item.ReferencePoint.X:F1}, Y={item.ReferencePoint.Y:F1}, θ={angleDeg:F2}° · {item.DetailLine}";
            BoundsD? bounds = item.Bounds.IsEmpty ? null : BoundsD.FromPoints(item.Bounds);

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
                ScoreSummary: item.BadgeLabel,
                PoseSummary: poseSummary,
                ElapsedMilliseconds: perItemMs,
                TargetBounds: bounds));
        }

        foreach (Guid roiId in roiOrder)
        {
            List<InspectionSuiteItemResult> group = roiGroups[roiId];
            bool hasStateRoi = _state.Rois.TryGetValue(roiId, out var stateRoi);
            RoiSuiteExecutionMode mode = hasStateRoi && stateRoi is not null
                ? GetRoiExecutionMode(stateRoi)
                : (_roiExecutionModes.TryGetValue(roiId, out var cachedMode) ? cachedMode : RoiSuiteExecutionMode.Default);

            string roiName = hasStateRoi && stateRoi is not null
                ? stateRoi.Name
                : group[0].Name;

            if (group.Count == 1)
            {
                var single = group[0];
                string singleVerdictBadge = single.IsPass ? "PASS" : "NG";
                string singleVerdictAccent = single.IsPass ? "#34D399" : "#F87171";
                RoiAssignmentRecorded?.Invoke(new RoiAlgorithmAssignment(
                    RoiId: roiId,
                    RoiName: roiName,
                    Tool: InspectionToolKind.InspectionSuite,
                    PatternMethod: null,
                    SuiteAlgorithm: algorithm,
                    AlgorithmBadge: badge,
                    AlgorithmAccent: accent,
                    ParameterSummary: ParameterPreviewSummary,
                    VerdictBadge: singleVerdictBadge,
                    VerdictAccent: singleVerdictAccent,
                    LastResultSummary: $"{singleVerdictBadge} · {single.BadgeLabel} · {single.DetailLine}",
                    SuiteExecutionMode: mode));
                continue;
            }

            int passCount = 0;
            for (int i = 0; i < group.Count; i++)
            {
                if (group[i].IsPass)
                {
                    passCount++;
                }
            }

            bool allPass = passCount == group.Count;
            string aggVerdictBadge = $"{(allPass ? "PASS" : "NG")} ({passCount}/{group.Count})";
            string aggVerdictAccent = allPass ? "#34D399" : "#F87171";
            string joinedBadges = string.Join(
                " / ",
                group.Select((x, idx) => $"#{idx + 1}[{(x.IsPass ? "OK" : "NG")}] {x.BadgeLabel}"));

            RoiAssignmentRecorded?.Invoke(new RoiAlgorithmAssignment(
                RoiId: roiId,
                RoiName: roiName,
                Tool: InspectionToolKind.InspectionSuite,
                PatternMethod: null,
                SuiteAlgorithm: algorithm,
                AlgorithmBadge: badge,
                AlgorithmAccent: accent,
                ParameterSummary: ParameterPreviewSummary,
                VerdictBadge: aggVerdictBadge,
                VerdictAccent: aggVerdictAccent,
                LastResultSummary: $"{aggVerdictBadge} · 총 {group.Count}개 검출 · {joinedBadges}",
                SuiteExecutionMode: mode));
        }
    }

    public InspectionSuiteParameters ExportParameters() => BuildParameters();

    public void ImportParameters(InspectionSuiteParameters? parameters)
    {
        if (parameters is null)
        {
            return;
        }

        _gaussianSigma = parameters.GaussianSigma;
        _minimumContrast = parameters.MinimumContrast;
        _thresholdIntensity = parameters.ThresholdIntensity;
        _morphologyRadius = parameters.MorphologyRadius;
        _variationStdMultiplier = parameters.VariationStdMultiplier;
        _variationEdgeMultiplier = parameters.VariationEdgeMultiplier;
        _nominalBeadWidth = parameters.NominalBeadWidth;
        _patchSize = parameters.PatchSize;
        _gridPitchMm = parameters.GridPitchMm;
        _lensK1 = parameters.LensK1;
        _metrologyMode = parameters.MetrologyMode;
        _polarityMode = parameters.PolarityMode;
        _caliperRakeCount = parameters.CaliperRakeCount;
        _caliperProjectionWidth = parameters.CaliperProjectionWidth;
        _goldenMinDefectAreaPixels = parameters.GoldenMinDefectAreaPixels;
        _blobMinimumAreaPixels = parameters.BlobMinimumAreaPixels;
        _fewShotOodSigmaMultiplier = parameters.FewShotOodSigmaMultiplier;
        _fewShotMaxSubClusters = parameters.FewShotMaxSubClusters;
        _padimRegularizationLambda = parameters.PadimRegularizationLambda;
        _cornerApexIgnoreMargin = parameters.CornerApexIgnoreMargin;
        _cornerRansacThresholdPixels = parameters.CornerRansacThresholdPixels;
        _cornerOrthogonalityToleranceDeg = parameters.CornerOrthogonalityToleranceDeg;
        _autoTeachMinGeometricScore = parameters.AutoTeachMinGeometricScore;
        _autoTeachMinAppearanceCosine = parameters.AutoTeachMinAppearanceCosine;
        _autoTeachMaxMahalanobis = parameters.AutoTeachMaxMahalanobis;
        _shadingTargetBaseGray = parameters.ShadingTargetBaseGray;
        _anisotropicDiffusionKappa = parameters.AnisotropicDiffusionKappa;
        _reconstructionMarkerRadius = parameters.ReconstructionMarkerRadius;
        _compositeExpectedSpanPx = parameters.CompositeExpectedSpanPx;
        _ellipseSpokeCount = parameters.EllipseSpokeCount;
        _ellipseMaxRmsErrorPx = parameters.EllipseMaxRmsErrorPx;
        _watershedThresholdIntensity = parameters.WatershedThresholdIntensity;
        _watershedMinPeakDistancePx = parameters.WatershedMinPeakDistancePx;
        _watershedMinAreaPixels = parameters.WatershedMinAreaPixels;
        _dataMatrixMinIsoContrast = parameters.DataMatrixMinIsoContrast;
        _dataMatrixMaxEccErrors = parameters.DataMatrixMaxEccErrors;
        _qrCodeMinIsoContrast = parameters.QrCodeMinIsoContrast;
        _qrCodeMaxEccErrors = parameters.QrCodeMaxEccErrors;
        _fftNotchOrGaborSigma = parameters.FftNotchOrGaborSigma;
        _fftPeakThresholdRatio = parameters.FftPeakThresholdRatio;
        _homomorphicGammaHigh = parameters.HomomorphicGammaHigh;
        _barcodeScanlineCount = parameters.BarcodeScanlineCount;
        _barcodeMinSymbolContrast = parameters.BarcodeMinSymbolContrast;
        _photometricSlantAngleDeg = parameters.PhotometricSlantAngleDeg;
        _photometricCurvatureThreshold = parameters.PhotometricCurvatureThreshold;
        _photometricMaxDefectPixels = parameters.PhotometricMaxDefectPixels;
        _gdtAngleToleranceDeg = parameters.GdtAngleToleranceDeg;
        _gdtConcentricityLimitPx = parameters.GdtConcentricityLimitPx;
        _gdtDistanceTolerancePx = parameters.GdtDistanceTolerancePx;
        _gdtGradientThreshold = parameters.GdtGradientThreshold;
        _ocrAllowedCharacters = parameters.OcrAllowedCharacters;
        _ppOcrMinConfidence = parameters.PpOcrMinConfidence;
        _ppOcrLanguageMode = parameters.PpOcrLanguageMode;
        _ppOcrPolarity = parameters.PpOcrPolarity;
        _ppOcrPipeline = parameters.PpOcrPipeline;
        _ppOcrPreset = parameters.PpOcrPreset;
        Notify(string.Empty);
        NotifyOptionValidationChanged();
    }

    public void SetRoiExecutionMode(Guid roiId, RoiSuiteExecutionMode mode)
    {
        if (roiId == Guid.Empty)
        {
            return;
        }

        _roiExecutionModes[roiId] = mode;
    }

    public RoiSuiteExecutionMode GetRoiExecutionMode(Roi roi)
    {
        ArgumentNullException.ThrowIfNull(roi);
        if (_roiExecutionModes.TryGetValue(roi.Id, out var existing))
        {
            return existing;
        }

        var migrated = LegacyRoiModeMigration.FromRoiName(roi.Name);
        _roiExecutionModes[roi.Id] = migrated;
        return migrated;
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
        AutoTeachMinGeometricScore: _autoTeachMinGeometricScore,
        AutoTeachMinAppearanceCosine: _autoTeachMinAppearanceCosine,
        AutoTeachMaxMahalanobis: _autoTeachMaxMahalanobis,
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
        GdtGradientThreshold: _gdtGradientThreshold,
        OcrAllowedCharacters: _ocrAllowedCharacters,
        PpOcrMinConfidence: _ppOcrMinConfidence,
        PpOcrLanguageMode: _ppOcrLanguageMode,
        PpOcrPolarity: _ppOcrPolarity,
        PpOcrPipeline: _ppOcrPipeline,
        PpOcrPreset: _ppOcrPreset,
        QrCodeMinIsoContrast: _qrCodeMinIsoContrast,
        QrCodeMaxEccErrors: _qrCodeMaxEccErrors);

    private bool TryGetSelectedVerifiedPpOcrText(
        out string text,
        out string roiName,
        out float meanConfidence)
    {
        text = string.Empty;
        roiName = string.Empty;
        meanConfidence = 0f;

        if (LastOutput is not { } output || output.Items.IsDefaultOrEmpty)
        {
            return false;
        }

        var selectedLeaves = GetSelectedLeafRois();
        if (selectedLeaves.Count != 1)
        {
            return false;
        }

        Guid selectedRoiId = selectedLeaves[0].Id;
        foreach (var item in output.Items)
        {
            if (item.RoiId != selectedRoiId)
            {
                continue;
            }

            if (!item.IsPass
                || item.OcrDiagnostic is not { } diag
                || string.IsNullOrWhiteSpace(diag.RecognizedText)
                || diag.MeanConfidence < _ppOcrMinConfidence
                || diag.MinConfidence < InspectionSuiteParameters.DefaultPpOcrMinCharacterConfidence)
            {
                return false;
            }

            text = NormalizeRecognizedTextForOcv(diag.RecognizedText);
            roiName = item.Name;
            meanConfidence = diag.MeanConfidence;
            return text.Length > 0;
        }

        return false;
    }

    private static string NormalizeRecognizedTextForOcv(string raw)
    {
        string singleLine = raw.Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();
        return singleLine;
    }

    private void CopyRecognizedPpOcrTextToOcvExpectedText()
    {
        if (!TryGetSelectedVerifiedPpOcrText(out string recognized, out string roiName, out float meanConfidence))
        {
            PresentWarning("단일 선택된 ROI에 대해 합격(신뢰도 기준 충족)된 PP-OCR 판독 결과가 있을 때만 기대 문자열로 채울 수 있습니다.");
            return;
        }

        if (_confirmApplyPpOcrToOcv is not null &&
            !_confirmApplyPpOcrToOcv.Invoke(roiName, recognized, meanConfidence))
        {
            Status = "PP-OCR 판독값 기대 문자열 반영을 취소했습니다.";
            Notify(nameof(Status));
            return;
        }

        SelectedRoiOcvExpectedText = recognized;
        Status = $"PP-OCRv4 판독 문자열 '{recognized}' (신뢰도 {meanConfidence:P1})을(를) 선택 ROI의 OCV 기대 문자열로 채웠습니다.";
        Notify(nameof(Status));
    }

    private void SyncSelectedRoiOcvSettingsFromState()
    {
        var selectedLeaves = GetSelectedLeafRois();
        string expected = selectedLeaves.Count == 1 ? selectedLeaves[0].Ocv.ExpectedText : string.Empty;
        string mask = selectedLeaves.Count == 1 ? selectedLeaves[0].Ocv.FormatMask : string.Empty;
        string fontId = selectedLeaves.Count == 1
            ? RoiOcvSettings.NormalizeFontId(selectedLeaves[0].Ocv.FontId)
            : RoiOcvSettings.DefaultFontId;
        _syncingRoiOcvFields = true;
        try
        {
            Set(ref _selectedRoiOcvExpectedText, expected, nameof(SelectedRoiOcvExpectedText));
            Set(ref _selectedRoiOcvFormatMask, mask, nameof(SelectedRoiOcvFormatMask));
            Set(ref _selectedRoiOcvFontId, fontId, nameof(SelectedRoiOcvFontId));
        }
        finally
        {
            _syncingRoiOcvFields = false;
        }

        Notify(nameof(CanEditSelectedRoiOcvSettings));
        Notify(nameof(OcvFontStatusBadge));
        ApplyPpOcrTextToOcvExpectedText.Refresh();
        TeachSelectedRoiOcvFont.Refresh();
        SaveOcvFontModel.Refresh();
    }

    private void CommitSelectedRoiOcvSettings()
    {
        var selectedLeaves = GetSelectedLeafRois();
        if (selectedLeaves.Count != 1)
        {
            return;
        }

        RoiOcvSettingsChangeRequested?.Invoke(
            [selectedLeaves[0].Id],
            new RoiOcvSettings(_selectedRoiOcvExpectedText, _selectedRoiOcvFormatMask, _selectedRoiOcvFontId));
    }

    private async Task TeachSelectedRoiOcvFontAsync()
    {
        if (_ocvService is null || _image is not { } image)
        {
            return;
        }

        var selectedRois = GetSelectedLeafRois();
        if (selectedRois.Count != 1)
        {
            PresentWarning("골든 폰트를 티칭할 단일 기준 리프 ROI 1개만 선택해 주세요.");
            return;
        }

        Roi targetRoi = selectedRois[0];
        string expectedText = _selectedRoiOcvExpectedText;
        if (string.IsNullOrWhiteSpace(expectedText))
        {
            expectedText = targetRoi.Ocv.ExpectedText;
        }

        string fontId = RoiOcvSettings.NormalizeFontId(_selectedRoiOcvFontId);
        var targetInput = ToRoiInput(targetRoi, classLabel: 0);
        long epoch = ++_epoch;
        CancelLivePreview();
        using var cts = BeginBusy($"골든 폰트 티칭 중… ({targetInput.Name})");
        try
        {
            OcvTeachingOutcome outcome = await _ocvService.TeachFromRoiAsync(
                image,
                _live,
                targetInput,
                expectedText,
                _selectedOcvPolarity,
                _ocvForegroundContrast,
                _ocvAppendTeachMode,
                cts.Token,
                fontId);

            if (epoch != _epoch || _disposed)
            {
                return;
            }

            if (outcome.IsSuccess)
            {
                OcvLastVerdictBadge = "TEACH OK";
                OcvLastVerdictAccent = TaughtFontOcvService.AccentOk;
                Status = outcome.StatusSummary;
                Results = $"{outcome.StatusSummary}\n극성={outcome.PolarityTag} · 조치 안내: {outcome.RecommendedAction}";
            }
            else
            {
                OcvLastVerdictBadge = "TEACH 거부";
                OcvLastVerdictAccent = TaughtFontOcvService.AccentReview;
                Status = outcome.StatusSummary;
                Results = $"{outcome.StatusSummary}\n원인: {outcome.FailureCause}\n권장 조치: {outcome.RecommendedAction}";
            }

            NotifyOcvStateChanged();
        }
        catch (OperationCanceledException)
        {
            if (epoch == _epoch)
            {
                Status = "골든 폰트 티칭이 취소되었습니다.";
                Notify(nameof(Status));
            }
        }
        finally
        {
            EndBusy(cts);
        }
    }

    private async Task VerifyRoisOcvAsync(bool selectedOnly)
    {
        if (_ocvService is null || _image is not { } image)
        {
            return;
        }

        if (!_ocvService.HasGoldenFont)
        {
            PresentWarning("등록된 골든 폰트가 없습니다. 먼저 기준 ROI에서 '폰트 티칭'을 실행하거나 폰트 파일을 불러오세요.");
            return;
        }

        var selectedRois = selectedOnly ? GetSelectedLeafRois() : [];
        var targetRois = selectedRois.Count > 0 ? selectedRois : GetAllLeafRois();
        if (targetRois.Count == 0)
        {
            PresentWarning("OCV 검증할 리프 ROI가 없습니다.");
            return;
        }

        var requests = new List<OcvRoiVerifyRequest>(targetRois.Count);
        bool singleSelectedEdit = selectedOnly && targetRois.Count == 1;
        for (int i = 0; i < targetRois.Count; i++)
        {
            Roi roi = targetRois[i];
            InspectionSuiteRoiInput input = ToRoiInput(roi, classLabel: 0);
            string expected = !string.IsNullOrWhiteSpace(roi.Ocv.ExpectedText)
                ? roi.Ocv.ExpectedText
                : (singleSelectedEdit ? _selectedRoiOcvExpectedText : string.Empty);
            string mask = !string.IsNullOrWhiteSpace(roi.Ocv.FormatMask)
                ? roi.Ocv.FormatMask
                : (singleSelectedEdit ? _selectedRoiOcvFormatMask : string.Empty);
            string fontId = RoiOcvSettings.NormalizeFontId(roi.Ocv.FontId);
            requests.Add(new OcvRoiVerifyRequest(
                input.RoiId,
                input.Name,
                input.Center,
                input.Width,
                input.Height,
                input.AngleRadians,
                input.Outline,
                expected,
                mask,
                fontId));
        }

        long epoch = ++_epoch;
        CancelLivePreview();
        using var cts = BeginBusy($"티칭 기반 OCV 인쇄 품질 검증 중… (ROI {requests.Count}개)");
        try
        {
            ImmutableArray<OcvRoiInspectionItem> items = await _ocvService.VerifyRoisAsync(
                image,
                _live,
                requests,
                _selectedOcvPolarity,
                _ocvForegroundContrast,
                cts.Token);

            if (epoch != _epoch || _disposed)
            {
                return;
            }

            ApplyOcvVerificationOutput(items);
        }
        catch (OperationCanceledException)
        {
            if (epoch == _epoch)
            {
                Status = "OCV 인쇄 품질 검증이 취소되었습니다.";
                Notify(nameof(Status));
            }
        }
        finally
        {
            EndBusy(cts);
        }
    }

    private void ApplyOcvVerificationOutput(ImmutableArray<OcvRoiInspectionItem> items)
    {
        OcvVerificationItems.Clear();
        int okCount = 0;
        int reviewCount = 0;
        int ngCount = 0;

        foreach (var item in items)
        {
            OcvVerificationItems.Add(item);
            switch (item.Verdict)
            {
                case OcvVerdictKind.Ok:
                    okCount++;
                    break;
                case OcvVerdictKind.Review:
                    reviewCount++;
                    break;
                default:
                    ngCount++;
                    break;
            }

            EmitOcvExecutionResult(item);
        }

        SelectedOcvVerificationItem = OcvVerificationItems.FirstOrDefault();

        if (ngCount > 0)
        {
            OcvLastVerdictBadge = $"NG {ngCount}건";
            OcvLastVerdictAccent = TaughtFontOcvService.AccentNg;
        }
        else if (reviewCount > 0)
        {
            OcvLastVerdictBadge = $"REVIEW {reviewCount}건 (경계 경고)";
            OcvLastVerdictAccent = TaughtFontOcvService.AccentReview;
        }
        else
        {
            OcvLastVerdictBadge = $"OK {okCount}/{items.Length}";
            OcvLastVerdictAccent = TaughtFontOcvService.AccentOk;
        }

        string summaryTitle = $"티칭 OCV 완료 · OK {okCount} / REVIEW(경고) {reviewCount} / NG(불량) {ngCount} (총 {items.Length}개)";
        _overlay = BuildOcvResultOverlay(items, summaryTitle);
        Status = summaryTitle;
        Results = string.Join(
            Environment.NewLine,
            items.Select((item, idx) =>
                $"[{idx + 1}] {item.RoiName} [{item.VerdictBadge}] 기대='{item.ExpectedText}' 관측='{item.DecodedText}' 평균={item.MeanScore:F3} 최소={item.MinScore:F3} · 원인={item.CauseSummary} · 조치={item.RecommendedAction}"));

        NotifyOcvStateChanged();
        Notify(nameof(Status));
        Notify(nameof(Results));
        Notify(nameof(Overlay));
        Locate.Refresh();
        Changed?.Invoke();
    }

    private static ResultOverlay BuildOcvResultOverlay(ImmutableArray<OcvRoiInspectionItem> items, string summaryTitle)
    {
        var markers = ImmutableArray.CreateBuilder<MatchMarker>(items.Length);
        bool anyNg = false;
        bool anyReview = false;

        foreach (var item in items)
        {
            Vector4 markerColor = item.Verdict switch
            {
                OcvVerdictKind.Ok => PassColor,
                OcvVerdictKind.Review => ReviewColor,
                _ => FailColor
            };
            anyNg |= item.IsNg;
            anyReview |= item.IsReview;

            var diagGroups = ImmutableArray.CreateBuilder<DiagnosticPointGroup>();
            if (!item.OkCharCenters.IsEmpty)
            {
                diagGroups.Add(new DiagnosticPointGroup(PassColor, item.OkCharCenters, HalfSize: 2.6));
            }
            if (!item.ReviewCharCenters.IsEmpty)
            {
                diagGroups.Add(new DiagnosticPointGroup(SecondaryFeatureColor, item.ReviewCharCenters, HalfSize: 2.8));
            }
            if (!item.NgCharCenters.IsEmpty)
            {
                diagGroups.Add(new DiagnosticPointGroup(DefectFeatureColor, item.NgCharCenters, HalfSize: 3.0));
            }

            markers.Add(new MatchMarker(
                Bounds: item.Bounds,
                ReferencePoint: item.ReferencePoint,
                AngleRadians: item.AngleRadians,
                Label: $"{item.RoiName}: {item.VerdictBadge} (min={item.MinScore:F2})",
                Color: markerColor,
                Features: item.OkCharCenters)
            {
                SourceRoiId = item.RoiId,
                AdditionalContours = item.CharContours,
                DiagnosticGroups = diagGroups.ToImmutable(),
                KeyFeatures = item.OkCharCenters,
                UnmatchedFeatures = item.NgCharCenters,
                BoundsColor = markerColor,
                BoundsThickness = 2.3
            });
        }

        Vector4 summaryColor = anyNg ? FailColor : (anyReview ? ReviewColor : PassColor);
        return new ResultOverlay(
            Summary: summaryTitle,
            Color: summaryColor,
            SearchBoundary: [],
            Matches: markers.MoveToImmutable(),
            ShowFeatures: true);
    }

    private void EmitOcvExecutionResult(OcvRoiInspectionItem item)
    {
        InspectionVerdict verdict = item.Verdict switch
        {
            OcvVerdictKind.Ok => InspectionVerdict.Pass,
            OcvVerdictKind.Review => InspectionVerdict.Review,
            _ => InspectionVerdict.Fail
        };

        string badge = "티칭 OCV";
        string accent = "#818CF8";
        string scoreSummary = $"평균 {item.MeanScore:F3} / 최소 {item.MinScore:F3}";
        double angleDeg = item.AngleRadians * (180.0 / Math.PI);
        string poseSummary = $"기대='{item.ExpectedText}' 관측='{item.DecodedText}' · 원인={item.CauseSummary} · 조치={item.RecommendedAction} (X={item.ReferencePoint.X:F1}, Y={item.ReferencePoint.Y:F1}, θ={angleDeg:F1}°)";
        BoundsD? bounds = item.Bounds.IsEmpty ? null : BoundsD.FromPoints(item.Bounds);

        if (item.RoiId != Guid.Empty)
        {
            RoiAssignmentRecorded?.Invoke(new RoiAlgorithmAssignment(
                RoiId: item.RoiId,
                RoiName: item.RoiName,
                Tool: InspectionToolKind.InspectionSuite,
                PatternMethod: null,
                SuiteAlgorithm: null,
                AlgorithmBadge: badge,
                AlgorithmAccent: accent,
                ParameterSummary: $"기대='{item.ExpectedText}' · 형식='{item.FormatMask}'",
                VerdictBadge: item.VerdictBadge,
                VerdictAccent: item.VerdictAccent,
                LastResultSummary: $"{item.VerdictBadge} · {scoreSummary} · {item.CauseSummary}"));
        }

        StructuredLogRecorded?.Invoke(new StructuredInspectionLogEntry(
            EntryId: Guid.NewGuid(),
            Timestamp: DateTimeOffset.Now,
            RoiId: item.RoiId == Guid.Empty ? null : item.RoiId,
            RoiName: item.RoiName,
            Tool: InspectionToolKind.InspectionSuite,
            AlgorithmBadge: badge,
            Verdict: verdict,
            VerdictBadge: item.VerdictBadge,
            VerdictAccent: item.VerdictAccent,
            ScoreSummary: scoreSummary,
            PoseSummary: poseSummary,
            ElapsedMilliseconds: item.ElapsedMicroseconds / 1000.0,
            TargetBounds: bounds));
    }

    public Dictionary<string, string> ExportOcvFontModelsForRecipe()
    {
        if (_ocvService is null)
        {
            return [];
        }

        var exported = _ocvService.ExportAllFontModelsJson();
        return new Dictionary<string, string>(exported, StringComparer.OrdinalIgnoreCase);
    }

    public void ImportOcvFontModelsFromRecipe(IReadOnlyDictionary<string, string>? fontModelsById)
    {
        if (_ocvService is null)
        {
            return;
        }

        _ocvService.ImportAllFontModelsJson(fontModelsById);
        NotifyOcvStateChanged();
    }

    private void SaveOcvFontModelFromDialog()
    {
        string fontId = RoiOcvSettings.NormalizeFontId(_selectedRoiOcvFontId);
        if (_ocvService is null || !_ocvService.HasGoldenFontForId(fontId) || _selectOcvFontSavePath is null)
        {
            return;
        }

        string? savePath = _selectOcvFontSavePath.Invoke();
        if (string.IsNullOrWhiteSpace(savePath))
        {
            return;
        }

        try
        {
            using var stream = File.Create(savePath);
            _ocvService.SaveFontModel(stream, fontId);
            Status = $"골든 폰트 저장 완료: {Path.GetFileName(savePath)} ({_ocvService.GetRegisteredCharactersSummary(fontId)})";
            Notify(nameof(Status));
        }
        catch (Exception ex)
        {
            PresentWarning($"골든 폰트 저장 실패: {ex.Message}");
        }
    }

    private void LoadOcvFontModelFromDialog()
    {
        if (_ocvService is null || _selectOcvFontOpenPath is null)
        {
            return;
        }

        string? openPath = _selectOcvFontOpenPath.Invoke();
        if (string.IsNullOrWhiteSpace(openPath))
        {
            return;
        }

        string fontId = RoiOcvSettings.NormalizeFontId(_selectedRoiOcvFontId);
        try
        {
            using var stream = File.OpenRead(openPath);
            _ocvService.LoadFontModel(stream, fontId);
            OcvLastVerdictBadge = "FONT LOADED";
            OcvLastVerdictAccent = TaughtFontOcvService.AccentOk;
            Status = $"골든 폰트 불러오기 완료: {Path.GetFileName(openPath)} ({_ocvService.GetRegisteredCharactersSummary(fontId)})";
            NotifyOcvStateChanged();
            Notify(nameof(Status));
        }
        catch (Exception ex)
        {
            PresentWarning($"골든 폰트 불러오기 실패: {ex.Message}");
        }
    }

    private void ClearOcvFontModelState()
    {
        if (_ocvService is null)
        {
            return;
        }

        _ocvService.ClearFontModel();
        OcvVerificationItems.Clear();
        SelectedOcvVerificationItem = null;
        OcvLastVerdictBadge = "READY";
        OcvLastVerdictAccent = "#38BDF8";
        Status = "등록된 골든 폰트와 OCV 검증 결과를 초기화했습니다.";
        NotifyOcvStateChanged();
        Notify(nameof(Status));
    }

    private void NotifyOcvStateChanged()
    {
        Notify(nameof(HasGoldenOcvFont));
        Notify(nameof(OcvFontStatusBadge));
        Notify(nameof(OcvLastVerdictBadge));
        Notify(nameof(OcvLastVerdictAccent));
        Notify(nameof(HasOcvVerificationItems));
        Notify(nameof(SelectedOcvCharacterItems));
        Notify(nameof(HasSelectedOcvCharacterItems));
        RefreshCommands();
    }

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
            var selectedPreview = GetSelectedLeafRois();
            if (selectedPreview.Count > 0)
            {
                return selectedPreview.Count <= 6 ? selectedPreview : selectedPreview.GetRange(0, 6);
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

        list.Sort(CompareLeafRoisReadingOrder);
        return list;
    }

    private List<Roi> GetAllLeafRois()
    {
        var list = _state.Rois.Values
            .Where(static roi => !roi.IsGroup)
            .ToList();
        list.Sort(CompareLeafRoisReadingOrder);
        return list;
    }

    private static int CompareLeafRoisReadingOrder(Roi a, Roi b)
    {
        const double ReadingRowBandHeightPx = 80.0;
        double bandA = Math.Round(a.Geometry.Center.Y / ReadingRowBandHeightPx);
        double bandB = Math.Round(b.Geometry.Center.Y / ReadingRowBandHeightPx);
        int bandCmp = bandA.CompareTo(bandB);
        if (bandCmp != 0)
        {
            return bandCmp;
        }

        int xCmp = a.Geometry.Center.X.CompareTo(b.Geometry.Center.X);
        if (xCmp != 0)
        {
            return xCmp;
        }

        return a.Id.CompareTo(b.Id);
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
            classLabel,
            ExplicitExecutionMode: GetRoiExecutionMode(roi));
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
        UpdateOcrDiagnosticViewState(LastOutput is { } outRes && outRes.Algorithm == _selectedAlgorithm ? outRes : null);
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
        Notify(nameof(IsAutoTeachingSelected));
        Notify(nameof(IsPpOcrOnnxSelected));
        Notify(nameof(IsPpOcrWired));
        Notify(nameof(PpOcrStatusBadge));
        Notify(nameof(IsAnyOcrSelected));
        Notify(nameof(IsPreprocessingShadingSelected));
        Notify(nameof(IsAdvancedMetrologyEllipseSelected));
        Notify(nameof(IsAdvancedBlobWatershedSelected));
        Notify(nameof(IsDataMatrix2DSelected));
        Notify(nameof(IsQrCode2DSelected));
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
        LoadOcrOcvSample.Refresh();
        LoadAutoTeachingSample.Refresh();
        ApplyPpOcrTextToOcvExpectedText.Refresh();
        SelectMetrologyCategory.Refresh();
        SelectSurface3DCategory.Refresh();
        SelectOcrCategory.Refresh();
        SelectBarcodeCategory.Refresh();
        SelectCodeOcrCategory.Refresh();
        SelectAiAutoTeachingCategory.Refresh();
        SelectPreprocessingCategory.Refresh();
        SelectAllCategories.Refresh();
        foreach (var option in Algorithms)
        {
            option.SelectCommand?.Refresh();
        }
        TeachSelectedRoiOcvFont.Refresh();
        VerifySelectedRoiOcv.Refresh();
        VerifyAllRoisOcv.Refresh();
        SaveOcvFontModel.Refresh();
        LoadOcvFontModel.Refresh();
        ClearOcvFontModel.Refresh();
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
