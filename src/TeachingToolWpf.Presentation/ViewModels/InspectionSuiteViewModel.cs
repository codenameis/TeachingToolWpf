using System.Collections.Immutable;
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

public sealed record InspectionSuiteAlgorithmOption(InspectionSuiteAlgorithmKind Kind, string Name);
public sealed record MetrologyModeOption(MetrologyMeasurementMode Mode, string Name);
public sealed record MetrologyPolarityOption(MetrologyPolarityMode Polarity, string Name);

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

        Algorithms =
        [
            new(InspectionSuiteAlgorithmKind.Metrology, InspectionSuiteService.GetAlgorithmShortName(InspectionSuiteAlgorithmKind.Metrology)),
            new(InspectionSuiteAlgorithmKind.GoldenVariation, InspectionSuiteService.GetAlgorithmShortName(InspectionSuiteAlgorithmKind.GoldenVariation)),
            new(InspectionSuiteAlgorithmKind.BlobRle, InspectionSuiteService.GetAlgorithmShortName(InspectionSuiteAlgorithmKind.BlobRle)),
            new(InspectionSuiteAlgorithmKind.Curvilinear, InspectionSuiteService.GetAlgorithmShortName(InspectionSuiteAlgorithmKind.Curvilinear)),
            new(InspectionSuiteAlgorithmKind.FewShot, InspectionSuiteService.GetAlgorithmShortName(InspectionSuiteAlgorithmKind.FewShot)),
            new(InspectionSuiteAlgorithmKind.PatchAnomaly, InspectionSuiteService.GetAlgorithmShortName(InspectionSuiteAlgorithmKind.PatchAnomaly)),
            new(InspectionSuiteAlgorithmKind.Calibration, InspectionSuiteService.GetAlgorithmShortName(InspectionSuiteAlgorithmKind.Calibration))
        ];

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
            () => IsIdle && _canExecute && _image is not null && GetSelectedLeafRois().Count > 0);
        RegisterDefectReference = new(() => RegisterSelectedRois(classLabel: 1),
            () => IsIdle && _canExecute && _image is not null && GetSelectedLeafRois().Count > 0);
        ClearReferences = new(ClearAllReferences,
            () => IsIdle && (_referenceRois.Count > 0 || _overlay is not null));
        ExecuteSelected = new(() => ExecuteRoisAsync(selectedOnly: true), error => PresentWarning(error.Message),
            () => IsIdle && _canExecute && _image is not null && (GetSelectedLeafRois().Count > 0 || GetAllLeafRois().Count > 0) && OptionsError.Length == 0);
        ExecuteAllRois = new(() => ExecuteRoisAsync(selectedOnly: false), error => PresentWarning(error.Message),
            () => IsIdle && _canExecute && _image is not null && GetAllLeafRois().Count > 0 && OptionsError.Length == 0);
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
    public IReadOnlyList<MetrologyModeOption> MetrologyModes { get; }
    public IReadOnlyList<MetrologyPolarityOption> PolarityModes { get; }
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
    public bool UsesReferenceRois => _selectedAlgorithm is InspectionSuiteAlgorithmKind.GoldenVariation
        or InspectionSuiteAlgorithmKind.FewShot
        or InspectionSuiteAlgorithmKind.PatchAnomaly;
    public bool UsesEdgeContrastParams => _selectedAlgorithm is InspectionSuiteAlgorithmKind.Metrology
        or InspectionSuiteAlgorithmKind.Curvilinear;

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
                _overlay = null;
                NotifyAlgorithmSelectionChanged();
                UpdateGeometricGuideAndSchedulePreview();
                RefreshCommands();
            }
        }
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
            $"표준편차 배수 k={_variationStdMultiplier:F1} · 에지 지터 허용 c={_variationEdgeMultiplier:F2}",
        InspectionSuiteAlgorithmKind.BlobRle =>
            $"이진화 임계 밝기={_thresholdIntensity} GV · 모폴로지 오프닝 반경 R={_morphologyRadius} px",
        InspectionSuiteAlgorithmKind.Curvilinear =>
            $"Steger σ={_gaussianSigma:F2} · 최소대비={_minimumContrast:F1} · 비드 기준폭={_nominalBeadWidth:F1} px",
        InspectionSuiteAlgorithmKind.FewShot =>
            $"다중 구역 기울기 모멘트 + SimpleShot + Ledoit-Wolf 마할라노비스 OOD 판정",
        InspectionSuiteAlgorithmKind.PatchAnomaly =>
            $"PaDiM 패치 크기={_patchSize}×{_patchSize} px · 공분산 마할라노비스 히트맵",
        InspectionSuiteAlgorithmKind.Calibration =>
            $"격자 피치={_gridPitchMm:F2} mm · 방사 왜곡 k1={_lensK1:F4}",
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

        _state = state;
        _lastRoisRef = state.Rois;
        _lastSelectionRef = state.Selection;
        _transformPreview = update.TransformPreview;
        _image = state.Image;
        _generation = update.Generation;
        _live = update.Live;
        _toolAvailable = update.ToolAvailable;
        _canExecute = update.CanExecute && update.ToolAvailable;

        if (imageChanged)
        {
            _epoch++;
            _cancellation?.Cancel();
            CancelLivePreview();
            _referenceRois.Clear();
            _overlay = null;
            _livePreview = null;
            LastOutput = null;
            ReferenceSummary = "등록된 기준 ROI 없음 (미등록 시 첫 번째 대상 ROI를 기준으로 자동 학습)";
            Results = "실행 결과 없음";
            Status = _image is null
                ? "상단 '현재 알고리즘 샘플 열기'를 누르거나 이미지를 열어 주세요."
                : "ROI를 선택·조절하면 실시간 프리뷰가 표시됩니다.";
            Notify(nameof(ReferenceSummary));
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

        int normalCount = _referenceRois.Count(r => r.ClassLabel == 0);
        int defectCount = _referenceRois.Count(r => r.ClassLabel != 0);
        ReferenceSummary = $"등록 기준 ROI 총 {_referenceRois.Count}개 (정상 {normalCount}개 · 불량 {defectCount}개)";
        Status = $"{selected.Count}개 ROI를 {(classLabel == 0 ? "정상/기준(Good)" : "불량(Defect)")} 샘플로 등록했습니다.";
        Notify(nameof(ReferenceSummary));
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

        int normalCount = _referenceRois.Count(r => r.ClassLabel == 0);
        int defectCount = _referenceRois.Count(r => r.ClassLabel != 0);
        ReferenceSummary = _referenceRois.Count > 0
            ? $"프리셋 기준 ROI 자동 등록됨: 총 {_referenceRois.Count}개 (정상 {normalCount}개 · 불량 {defectCount}개)"
            : "등록된 기준 ROI 없음 (미등록 시 첫 번째 대상 ROI를 기준으로 자동 학습)";
        Notify(nameof(ReferenceSummary));
        RefreshCommands();
    }

    private void RequestSampleLoad(InspectionSuiteAlgorithmKind kind)
    {
        SelectedAlgorithm = kind;
        SampleLoadRequested?.Invoke(kind);
    }

    private void ClearAllReferences()
    {
        _referenceRois.Clear();
        _overlay = null;
        _livePreview = null;
        LastOutput = null;
        ReferenceSummary = "등록된 기준 ROI 없음 (미등록 시 첫 번째 대상 ROI를 기준으로 자동 학습)";
        Results = "실행 결과 없음";
        Status = "등록된 기준 ROI와 결과 오버레이를 초기화했습니다.";
        Notify(nameof(ReferenceSummary));
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

        // 1. 0ms 동기 기하 가이드 오버레이 생성 (ROI 드래그·회전 중에도 60FPS로 캘리퍼 스캔선/레이크/스포크 표시)
        if (_overlay is null)
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
        if (triggerAsyncMeasurement && _autoPreview && _showOverlay && IsIdle && _image is not null && OptionsError.Length == 0)
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
            Status = $"[실시간 프리뷰] {output.SummaryTitle}";
            Results = output.DetailedReport;
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
        CaliperProjectionWidth: _caliperProjectionWidth);

    private List<Roi> GetPreviewTargetRois()
    {
        var selected = GetSelectedLeafRois();
        if (selected.Count > 0)
        {
            return selected.Count <= 6 ? selected : selected.GetRange(0, 6);
        }

        var all = GetAllLeafRois();
        if (_referenceRois.Count > 0)
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
        Notify(nameof(AlgorithmDescription));
        Notify(nameof(AlgorithmUsageGuide));
        Notify(nameof(ParameterPreviewSummary));
        Notify(nameof(IsMetrologySelected));
        Notify(nameof(IsGoldenVariationSelected));
        Notify(nameof(IsBlobRleSelected));
        Notify(nameof(IsCurvilinearSelected));
        Notify(nameof(IsFewShotSelected));
        Notify(nameof(IsPatchAnomalySelected));
        Notify(nameof(IsCalibrationSelected));
        Notify(nameof(UsesReferenceRois));
        Notify(nameof(UsesEdgeContrastParams));
    }

    private void NotifyOptionValidationChanged()
    {
        _overlay = null;
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
