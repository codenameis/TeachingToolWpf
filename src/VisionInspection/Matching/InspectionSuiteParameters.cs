using System.Text.Json;
using System.Text.Json.Serialization;
using VisionAlgorithm.OpenSrc.IndustrialOcrOcv;
using VisionOcr.PpOcr;

namespace TeachingToolWpf.Imaging.Matching;

/// <summary>
/// 검사 스위트 알고리즘별 응집된 옵션 레코드 묶음.
/// 각 알고리즘은 자신이 소비하는 옵션 레코드(<see cref="Metrology"/>, <see cref="Blob"/>, <see cref="PpOcr"/> 등)만 참조하며,
/// 기존 호출부 및 레시피 JSON 직렬화 형식을 하위 호환 로더(<see cref="FromJson"/>)로 보존한다.
/// </summary>
public sealed record InspectionSuiteParameters
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public const OcrAllowedCharacterSet DefaultOcrAllowedCharacters = PpOcrSuiteOptions.DefaultAllowedCharacters;
    public const float DefaultPpOcrMinConfidence = PpOcrSuiteOptions.DefaultMinConfidence;
    public const float DefaultPpOcrMinCharacterConfidence = PpOcrSuiteOptions.DefaultMinCharacterConfidence;
    public const PpOcrLanguage DefaultPpOcrLanguageMode = PpOcrSuiteOptions.DefaultLanguageMode;
    public const PpOcrPolarityMode DefaultPpOcrPolarity = PpOcrSuiteOptions.DefaultPolarity;
    public const PpOcrPipelineMode DefaultPpOcrPipeline = PpOcrSuiteOptions.DefaultPipeline;
    public const PpOcrSpeedPreset DefaultPpOcrPreset = PpOcrSuiteOptions.DefaultPreset;
    public const bool DefaultIsInteractivePreview = PpOcrSuiteOptions.DefaultIsInteractivePreview;

    public MetrologyOptions Metrology { get; init; } = new();
    public GoldenVariationOptions GoldenVariation { get; init; } = new();
    public BlobRleOptions Blob { get; init; } = new();
    public CurvilinearOptions Curvilinear { get; init; } = new();
    public FewShotSuiteOptions FewShot { get; init; } = new();
    public PatchAnomalyOptions PatchAnomaly { get; init; } = new();
    public CalibrationOptions Calibration { get; init; } = new();
    public CornerAlignmentOptions CornerAlignment { get; init; } = new();
    public AutoTeachingOptions AutoTeaching { get; init; } = new();
    public ShadingReconOptions ShadingRecon { get; init; } = new();
    public AdvancedMetrologyOptions AdvancedMetrology { get; init; } = new();
    public AdvancedBlobOptions AdvancedBlob { get; init; } = new();
    public DataMatrixOptions DataMatrix { get; init; } = new();
    public FrequencyFilterOptions FrequencyFilter { get; init; } = new();
    public Barcode1DOptions Barcode { get; init; } = new();
    public PhotometricStereoOptions PhotometricStereo { get; init; } = new();
    public GeometricTolerancingOptions GeometricTolerancing { get; init; } = new();
    public PpOcrSuiteOptions PpOcr { get; init; } = new();
    public QrCodeOptions QrCode { get; init; } = new();

    public InspectionSuiteParameters()
    {
    }

    public InspectionSuiteParameters(
        float GaussianSigma = MetrologyOptions.DefaultGaussianSigma,
        float MinimumContrast = MetrologyOptions.DefaultMinimumContrast,
        int ThresholdIntensity = BlobRleOptions.DefaultThresholdIntensity,
        int MorphologyRadius = BlobRleOptions.DefaultMorphologyRadius,
        float VariationStdMultiplier = GoldenVariationOptions.DefaultVariationStdMultiplier,
        float VariationEdgeMultiplier = GoldenVariationOptions.DefaultVariationEdgeMultiplier,
        float NominalBeadWidth = CurvilinearOptions.DefaultNominalBeadWidth,
        int PatchSize = PatchAnomalyOptions.DefaultPatchSize,
        float GridPitchMm = CalibrationOptions.DefaultGridPitchMm,
        float LensK1 = CalibrationOptions.DefaultLensK1,
        MetrologyMeasurementMode MetrologyMode = MetrologyMeasurementMode.Auto,
        MetrologyPolarityMode PolarityMode = MetrologyPolarityMode.Any,
        int CaliperRakeCount = MetrologyOptions.DefaultCaliperRakeCount,
        float CaliperProjectionWidth = MetrologyOptions.DefaultCaliperProjectionWidth,
        int GoldenMinDefectAreaPixels = GoldenVariationOptions.DefaultMinimumDefectAreaPixels,
        int BlobMinimumAreaPixels = BlobRleOptions.DefaultMinimumAreaPixels,
        float FewShotOodSigmaMultiplier = FewShotSuiteOptions.DefaultOodSigmaMultiplier,
        int FewShotMaxSubClusters = FewShotSuiteOptions.DefaultMaxSubClusters,
        float PadimRegularizationLambda = PatchAnomalyOptions.DefaultRegularizationLambda,
        float CornerApexIgnoreMargin = CornerAlignmentOptions.DefaultApexIgnoreMargin,
        float CornerRansacThresholdPixels = CornerAlignmentOptions.DefaultRansacThresholdPixels,
        float CornerOrthogonalityToleranceDeg = CornerAlignmentOptions.DefaultOrthogonalityToleranceDeg,
        float AutoTeachMinGeometricScore = AutoTeachingOptions.DefaultMinGeometricScore,
        float AutoTeachMinAppearanceCosine = AutoTeachingOptions.DefaultMinAppearanceCosine,
        float AutoTeachMaxMahalanobis = AutoTeachingOptions.DefaultMaxMahalanobis,
        int ShadingTargetBaseGray = ShadingReconOptions.DefaultTargetBaseGray,
        float AnisotropicDiffusionKappa = ShadingReconOptions.DefaultAnisotropicDiffusionKappa,
        int ReconstructionMarkerRadius = ShadingReconOptions.DefaultReconstructionMarkerRadius,
        float CompositeExpectedSpanPx = AdvancedMetrologyOptions.DefaultCompositeExpectedSpanPx,
        int EllipseSpokeCount = AdvancedMetrologyOptions.DefaultEllipseSpokeCount,
        float EllipseMaxRmsErrorPx = AdvancedMetrologyOptions.DefaultEllipseMaxRmsErrorPx,
        int WatershedThresholdIntensity = AdvancedBlobOptions.DefaultThresholdIntensity,
        float WatershedMinPeakDistancePx = AdvancedBlobOptions.DefaultMinPeakDistancePx,
        int WatershedMinAreaPixels = AdvancedBlobOptions.DefaultMinimumAreaPixels,
        float DataMatrixMinIsoContrast = DataMatrixOptions.DefaultMinIsoContrast,
        int DataMatrixMaxEccErrors = DataMatrixOptions.DefaultMaxEccErrors,
        float FftNotchOrGaborSigma = FrequencyFilterOptions.DefaultNotchOrGaborSigma,
        float FftPeakThresholdRatio = FrequencyFilterOptions.DefaultPeakThresholdRatio,
        float HomomorphicGammaHigh = FrequencyFilterOptions.DefaultHomomorphicGammaHigh,
        int BarcodeScanlineCount = Barcode1DOptions.DefaultScanlineCount,
        float BarcodeMinSymbolContrast = Barcode1DOptions.DefaultMinSymbolContrast,
        float PhotometricSlantAngleDeg = PhotometricStereoOptions.DefaultSlantAngleDeg,
        float PhotometricCurvatureThreshold = PhotometricStereoOptions.DefaultCurvatureThreshold,
        int PhotometricMaxDefectPixels = PhotometricStereoOptions.DefaultMaxDefectPixels,
        float GdtAngleToleranceDeg = GeometricTolerancingOptions.DefaultAngleToleranceDeg,
        float GdtConcentricityLimitPx = GeometricTolerancingOptions.DefaultConcentricityLimitPx,
        float GdtDistanceTolerancePx = GeometricTolerancingOptions.DefaultDistanceTolerancePx,
        float GdtGradientThreshold = GeometricTolerancingOptions.DefaultGradientThreshold,
        OcrAllowedCharacterSet OcrAllowedCharacters = DefaultOcrAllowedCharacters,
        float PpOcrMinConfidence = DefaultPpOcrMinConfidence,
        float PpOcrMinCharacterConfidence = DefaultPpOcrMinCharacterConfidence,
        PpOcrLanguage PpOcrLanguageMode = DefaultPpOcrLanguageMode,
        PpOcrPolarityMode PpOcrPolarity = DefaultPpOcrPolarity,
        PpOcrPipelineMode PpOcrPipeline = DefaultPpOcrPipeline,
        PpOcrSpeedPreset PpOcrPreset = DefaultPpOcrPreset,
        bool IsInteractivePreview = DefaultIsInteractivePreview,
        float QrCodeMinIsoContrast = QrCodeOptions.DefaultMinIsoContrast,
        int QrCodeMaxEccErrors = QrCodeOptions.DefaultMaxEccErrors)
    {
        Metrology = new MetrologyOptions(GaussianSigma, MinimumContrast, MetrologyMode, PolarityMode, CaliperRakeCount, CaliperProjectionWidth);
        GoldenVariation = new GoldenVariationOptions(VariationStdMultiplier, VariationEdgeMultiplier, GoldenMinDefectAreaPixels);
        Blob = new BlobRleOptions(ThresholdIntensity, MorphologyRadius, BlobMinimumAreaPixels);
        Curvilinear = new CurvilinearOptions(GaussianSigma, MinimumContrast, NominalBeadWidth);
        FewShot = new FewShotSuiteOptions(FewShotOodSigmaMultiplier, FewShotMaxSubClusters);
        PatchAnomaly = new PatchAnomalyOptions(PatchSize, PadimRegularizationLambda);
        Calibration = new CalibrationOptions(GridPitchMm, LensK1);
        CornerAlignment = new CornerAlignmentOptions(GaussianSigma, MinimumContrast, CaliperRakeCount, CaliperProjectionWidth, CornerApexIgnoreMargin, CornerRansacThresholdPixels, CornerOrthogonalityToleranceDeg);
        AutoTeaching = new AutoTeachingOptions(AutoTeachMinGeometricScore, AutoTeachMinAppearanceCosine, AutoTeachMaxMahalanobis);
        ShadingRecon = new ShadingReconOptions(ShadingTargetBaseGray, AnisotropicDiffusionKappa, ReconstructionMarkerRadius, BlobMinimumAreaPixels);
        AdvancedMetrology = new AdvancedMetrologyOptions(GaussianSigma, MinimumContrast, CaliperProjectionWidth, CaliperRakeCount, CompositeExpectedSpanPx, EllipseSpokeCount, EllipseMaxRmsErrorPx);
        AdvancedBlob = new AdvancedBlobOptions(WatershedThresholdIntensity, WatershedMinPeakDistancePx, WatershedMinAreaPixels);
        DataMatrix = new DataMatrixOptions(DataMatrixMinIsoContrast, DataMatrixMaxEccErrors);
        FrequencyFilter = new FrequencyFilterOptions(FftNotchOrGaborSigma, FftPeakThresholdRatio, HomomorphicGammaHigh, BlobMinimumAreaPixels);
        Barcode = new Barcode1DOptions(BarcodeScanlineCount, BarcodeMinSymbolContrast);
        PhotometricStereo = new PhotometricStereoOptions(PhotometricSlantAngleDeg, PhotometricCurvatureThreshold, PhotometricMaxDefectPixels);
        GeometricTolerancing = new GeometricTolerancingOptions(GdtAngleToleranceDeg, GdtConcentricityLimitPx, GdtDistanceTolerancePx, GdtGradientThreshold);
        PpOcr = new PpOcrSuiteOptions(OcrAllowedCharacters, PpOcrMinConfidence, PpOcrMinCharacterConfidence, PpOcrLanguageMode, PpOcrPolarity, PpOcrPipeline, PpOcrPreset, IsInteractivePreview);
        QrCode = new QrCodeOptions(QrCodeMinIsoContrast, QrCodeMaxEccErrors);
    }

    [JsonIgnore]
    public float GaussianSigma
    {
        get => Metrology.GaussianSigma;
        init
        {
            Metrology = Metrology with { GaussianSigma = value };
            Curvilinear = Curvilinear with { GaussianSigma = value };
            CornerAlignment = CornerAlignment with { GaussianSigma = value };
            AdvancedMetrology = AdvancedMetrology with { GaussianSigma = value };
        }
    }

    [JsonIgnore]
    public float MinimumContrast
    {
        get => Metrology.MinimumContrast;
        init
        {
            Metrology = Metrology with { MinimumContrast = value };
            Curvilinear = Curvilinear with { MinimumContrast = value };
            CornerAlignment = CornerAlignment with { MinimumContrast = value };
            AdvancedMetrology = AdvancedMetrology with { MinimumContrast = value };
        }
    }

    [JsonIgnore]
    public int ThresholdIntensity
    {
        get => Blob.ThresholdIntensity;
        init => Blob = Blob with { ThresholdIntensity = value };
    }

    [JsonIgnore]
    public int MorphologyRadius
    {
        get => Blob.MorphologyRadius;
        init => Blob = Blob with { MorphologyRadius = value };
    }

    [JsonIgnore]
    public float VariationStdMultiplier
    {
        get => GoldenVariation.VariationStdMultiplier;
        init => GoldenVariation = GoldenVariation with { VariationStdMultiplier = value };
    }

    [JsonIgnore]
    public float VariationEdgeMultiplier
    {
        get => GoldenVariation.VariationEdgeMultiplier;
        init => GoldenVariation = GoldenVariation with { VariationEdgeMultiplier = value };
    }

    [JsonIgnore]
    public float NominalBeadWidth
    {
        get => Curvilinear.NominalBeadWidth;
        init => Curvilinear = Curvilinear with { NominalBeadWidth = value };
    }

    [JsonIgnore]
    public int PatchSize
    {
        get => PatchAnomaly.PatchSize;
        init => PatchAnomaly = PatchAnomaly with { PatchSize = value };
    }

    [JsonIgnore]
    public float GridPitchMm
    {
        get => Calibration.GridPitchMm;
        init => Calibration = Calibration with { GridPitchMm = value };
    }

    [JsonIgnore]
    public float LensK1
    {
        get => Calibration.LensK1;
        init => Calibration = Calibration with { LensK1 = value };
    }

    [JsonIgnore]
    public MetrologyMeasurementMode MetrologyMode
    {
        get => Metrology.Mode;
        init => Metrology = Metrology with { Mode = value };
    }

    [JsonIgnore]
    public MetrologyPolarityMode PolarityMode
    {
        get => Metrology.PolarityMode;
        init => Metrology = Metrology with { PolarityMode = value };
    }

    [JsonIgnore]
    public int CaliperRakeCount
    {
        get => Metrology.CaliperRakeCount;
        init
        {
            Metrology = Metrology with { CaliperRakeCount = value };
            CornerAlignment = CornerAlignment with { CaliperRakeCount = value };
            AdvancedMetrology = AdvancedMetrology with { CaliperRakeCount = value };
        }
    }

    [JsonIgnore]
    public float CaliperProjectionWidth
    {
        get => Metrology.CaliperProjectionWidth;
        init
        {
            Metrology = Metrology with { CaliperProjectionWidth = value };
            CornerAlignment = CornerAlignment with { CaliperProjectionWidth = value };
            AdvancedMetrology = AdvancedMetrology with { CaliperProjectionWidth = value };
        }
    }

    [JsonIgnore]
    public int GoldenMinDefectAreaPixels
    {
        get => GoldenVariation.MinimumDefectAreaPixels;
        init => GoldenVariation = GoldenVariation with { MinimumDefectAreaPixels = value };
    }

    [JsonIgnore]
    public int BlobMinimumAreaPixels
    {
        get => Blob.MinimumAreaPixels;
        init
        {
            Blob = Blob with { MinimumAreaPixels = value };
            ShadingRecon = ShadingRecon with { MinimumBlobAreaPixels = value };
            FrequencyFilter = FrequencyFilter with { MinimumBlobAreaPixels = value };
        }
    }

    [JsonIgnore]
    public float FewShotOodSigmaMultiplier
    {
        get => FewShot.OodSigmaMultiplier;
        init => FewShot = FewShot with { OodSigmaMultiplier = value };
    }

    [JsonIgnore]
    public int FewShotMaxSubClusters
    {
        get => FewShot.MaxSubClusters;
        init => FewShot = FewShot with { MaxSubClusters = value };
    }

    [JsonIgnore]
    public float PadimRegularizationLambda
    {
        get => PatchAnomaly.RegularizationLambda;
        init => PatchAnomaly = PatchAnomaly with { RegularizationLambda = value };
    }

    [JsonIgnore]
    public float CornerApexIgnoreMargin
    {
        get => CornerAlignment.ApexIgnoreMargin;
        init => CornerAlignment = CornerAlignment with { ApexIgnoreMargin = value };
    }

    [JsonIgnore]
    public float CornerRansacThresholdPixels
    {
        get => CornerAlignment.RansacThresholdPixels;
        init => CornerAlignment = CornerAlignment with { RansacThresholdPixels = value };
    }

    [JsonIgnore]
    public float CornerOrthogonalityToleranceDeg
    {
        get => CornerAlignment.OrthogonalityToleranceDeg;
        init => CornerAlignment = CornerAlignment with { OrthogonalityToleranceDeg = value };
    }

    [JsonIgnore]
    public float AutoTeachMinGeometricScore
    {
        get => AutoTeaching.MinGeometricScore;
        init => AutoTeaching = AutoTeaching with { MinGeometricScore = value };
    }

    [JsonIgnore]
    public float AutoTeachMinAppearanceCosine
    {
        get => AutoTeaching.MinAppearanceCosine;
        init => AutoTeaching = AutoTeaching with { MinAppearanceCosine = value };
    }

    [JsonIgnore]
    public float AutoTeachMaxMahalanobis
    {
        get => AutoTeaching.MaxMahalanobis;
        init => AutoTeaching = AutoTeaching with { MaxMahalanobis = value };
    }

    [JsonIgnore]
    public int ShadingTargetBaseGray
    {
        get => ShadingRecon.TargetBaseGray;
        init => ShadingRecon = ShadingRecon with { TargetBaseGray = value };
    }

    [JsonIgnore]
    public float AnisotropicDiffusionKappa
    {
        get => ShadingRecon.AnisotropicDiffusionKappa;
        init => ShadingRecon = ShadingRecon with { AnisotropicDiffusionKappa = value };
    }

    [JsonIgnore]
    public int ReconstructionMarkerRadius
    {
        get => ShadingRecon.ReconstructionMarkerRadius;
        init => ShadingRecon = ShadingRecon with { ReconstructionMarkerRadius = value };
    }

    [JsonIgnore]
    public float CompositeExpectedSpanPx
    {
        get => AdvancedMetrology.CompositeExpectedSpanPx;
        init => AdvancedMetrology = AdvancedMetrology with { CompositeExpectedSpanPx = value };
    }

    [JsonIgnore]
    public int EllipseSpokeCount
    {
        get => AdvancedMetrology.EllipseSpokeCount;
        init => AdvancedMetrology = AdvancedMetrology with { EllipseSpokeCount = value };
    }

    [JsonIgnore]
    public float EllipseMaxRmsErrorPx
    {
        get => AdvancedMetrology.EllipseMaxRmsErrorPx;
        init => AdvancedMetrology = AdvancedMetrology with { EllipseMaxRmsErrorPx = value };
    }

    [JsonIgnore]
    public int WatershedThresholdIntensity
    {
        get => AdvancedBlob.ThresholdIntensity;
        init => AdvancedBlob = AdvancedBlob with { ThresholdIntensity = value };
    }

    [JsonIgnore]
    public float WatershedMinPeakDistancePx
    {
        get => AdvancedBlob.MinPeakDistancePx;
        init => AdvancedBlob = AdvancedBlob with { MinPeakDistancePx = value };
    }

    [JsonIgnore]
    public int WatershedMinAreaPixels
    {
        get => AdvancedBlob.MinimumAreaPixels;
        init => AdvancedBlob = AdvancedBlob with { MinimumAreaPixels = value };
    }

    [JsonIgnore]
    public float DataMatrixMinIsoContrast
    {
        get => DataMatrix.MinIsoContrast;
        init => DataMatrix = DataMatrix with { MinIsoContrast = value };
    }

    [JsonIgnore]
    public int DataMatrixMaxEccErrors
    {
        get => DataMatrix.MaxEccErrors;
        init => DataMatrix = DataMatrix with { MaxEccErrors = value };
    }

    [JsonIgnore]
    public float QrCodeMinIsoContrast
    {
        get => QrCode.MinIsoContrast;
        init => QrCode = QrCode with { MinIsoContrast = value };
    }

    [JsonIgnore]
    public int QrCodeMaxEccErrors
    {
        get => QrCode.MaxEccErrors;
        init => QrCode = QrCode with { MaxEccErrors = value };
    }

    [JsonIgnore]
    public float FftNotchOrGaborSigma
    {
        get => FrequencyFilter.NotchOrGaborSigma;
        init => FrequencyFilter = FrequencyFilter with { NotchOrGaborSigma = value };
    }

    [JsonIgnore]
    public float FftPeakThresholdRatio
    {
        get => FrequencyFilter.PeakThresholdRatio;
        init => FrequencyFilter = FrequencyFilter with { PeakThresholdRatio = value };
    }

    [JsonIgnore]
    public float HomomorphicGammaHigh
    {
        get => FrequencyFilter.HomomorphicGammaHigh;
        init => FrequencyFilter = FrequencyFilter with { HomomorphicGammaHigh = value };
    }

    [JsonIgnore]
    public int BarcodeScanlineCount
    {
        get => Barcode.ScanlineCount;
        init => Barcode = Barcode with { ScanlineCount = value };
    }

    [JsonIgnore]
    public float BarcodeMinSymbolContrast
    {
        get => Barcode.MinSymbolContrast;
        init => Barcode = Barcode with { MinSymbolContrast = value };
    }

    [JsonIgnore]
    public float PhotometricSlantAngleDeg
    {
        get => PhotometricStereo.SlantAngleDeg;
        init => PhotometricStereo = PhotometricStereo with { SlantAngleDeg = value };
    }

    [JsonIgnore]
    public float PhotometricCurvatureThreshold
    {
        get => PhotometricStereo.CurvatureThreshold;
        init => PhotometricStereo = PhotometricStereo with { CurvatureThreshold = value };
    }

    [JsonIgnore]
    public int PhotometricMaxDefectPixels
    {
        get => PhotometricStereo.MaxDefectPixels;
        init => PhotometricStereo = PhotometricStereo with { MaxDefectPixels = value };
    }

    [JsonIgnore]
    public float GdtAngleToleranceDeg
    {
        get => GeometricTolerancing.AngleToleranceDeg;
        init => GeometricTolerancing = GeometricTolerancing with { AngleToleranceDeg = value };
    }

    [JsonIgnore]
    public float GdtConcentricityLimitPx
    {
        get => GeometricTolerancing.ConcentricityLimitPx;
        init => GeometricTolerancing = GeometricTolerancing with { ConcentricityLimitPx = value };
    }

    [JsonIgnore]
    public float GdtDistanceTolerancePx
    {
        get => GeometricTolerancing.DistanceTolerancePx;
        init => GeometricTolerancing = GeometricTolerancing with { DistanceTolerancePx = value };
    }

    [JsonIgnore]
    public float GdtGradientThreshold
    {
        get => GeometricTolerancing.GradientThreshold;
        init => GeometricTolerancing = GeometricTolerancing with { GradientThreshold = value };
    }

    [JsonIgnore]
    public OcrAllowedCharacterSet OcrAllowedCharacters
    {
        get => PpOcr.AllowedCharacters;
        init => PpOcr = PpOcr with { AllowedCharacters = value };
    }

    [JsonIgnore]
    public float PpOcrMinConfidence
    {
        get => PpOcr.MinConfidence;
        init => PpOcr = PpOcr with { MinConfidence = value };
    }

    [JsonIgnore]
    public float PpOcrMinCharacterConfidence
    {
        get => PpOcr.MinCharacterConfidence;
        init => PpOcr = PpOcr with { MinCharacterConfidence = value };
    }

    [JsonIgnore]
    public PpOcrLanguage PpOcrLanguageMode
    {
        get => PpOcr.LanguageMode;
        init => PpOcr = PpOcr with { LanguageMode = value };
    }

    [JsonIgnore]
    public PpOcrPolarityMode PpOcrPolarity
    {
        get => PpOcr.Polarity;
        init => PpOcr = PpOcr with { Polarity = value };
    }

    [JsonIgnore]
    public PpOcrPipelineMode PpOcrPipeline
    {
        get => PpOcr.Pipeline;
        init => PpOcr = PpOcr with { Pipeline = value };
    }

    [JsonIgnore]
    public PpOcrSpeedPreset PpOcrPreset
    {
        get => PpOcr.Preset;
        init => PpOcr = PpOcr with { Preset = value };
    }

    [JsonIgnore]
    public bool IsInteractivePreview
    {
        get => PpOcr.IsInteractivePreview;
        init => PpOcr = PpOcr with { IsInteractivePreview = value };
    }

    public InspectionSuiteParameters Validate() => new()
    {
        Metrology = Metrology.Validate(),
        GoldenVariation = GoldenVariation.Validate(),
        Blob = Blob.Validate(),
        Curvilinear = Curvilinear.Validate(),
        FewShot = FewShot.Validate(),
        PatchAnomaly = PatchAnomaly.Validate(),
        Calibration = Calibration.Validate(),
        CornerAlignment = CornerAlignment.Validate(),
        AutoTeaching = AutoTeaching.Validate(),
        ShadingRecon = ShadingRecon.Validate(),
        AdvancedMetrology = AdvancedMetrology.Validate(),
        AdvancedBlob = AdvancedBlob.Validate(),
        DataMatrix = DataMatrix.Validate(),
        FrequencyFilter = FrequencyFilter.Validate(),
        Barcode = Barcode.Validate(),
        PhotometricStereo = PhotometricStereo.Validate(),
        GeometricTolerancing = GeometricTolerancing.Validate(),
        PpOcr = PpOcr.Validate(),
        QrCode = QrCode.Validate()
    };

    public string ToJson() => JsonSerializer.Serialize(this, SerializerOptions);

    /// <summary>
    /// 신규 중첩 옵션 JSON과 기존 플랫 55필드 JSON 형식을 모두 읽어 들이는 하위 호환 로더.
    /// </summary>
    public static InspectionSuiteParameters FromJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        using var doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("InspectionSuiteParameters JSON 루트는 객체여야 합니다.");
        }

        bool hasNestedSection = root.TryGetProperty(nameof(Metrology), out _)
            || root.TryGetProperty(nameof(Blob), out _)
            || root.TryGetProperty(nameof(PpOcr), out _);
        if (hasNestedSection)
        {
            return JsonSerializer.Deserialize<InspectionSuiteParameters>(json, SerializerOptions) ?? new InspectionSuiteParameters();
        }

        // [Backward Compatibility]: 기존 플랫 55필드 JSON 포맷 로더
        LegacyFlatSuiteParameters flat = JsonSerializer.Deserialize<LegacyFlatSuiteParameters>(json, SerializerOptions) ?? new LegacyFlatSuiteParameters();
        return flat.ToStructuredParameters();
    }

    private sealed record LegacyFlatSuiteParameters(
        float GaussianSigma = MetrologyOptions.DefaultGaussianSigma,
        float MinimumContrast = MetrologyOptions.DefaultMinimumContrast,
        int ThresholdIntensity = BlobRleOptions.DefaultThresholdIntensity,
        int MorphologyRadius = BlobRleOptions.DefaultMorphologyRadius,
        float VariationStdMultiplier = GoldenVariationOptions.DefaultVariationStdMultiplier,
        float VariationEdgeMultiplier = GoldenVariationOptions.DefaultVariationEdgeMultiplier,
        float NominalBeadWidth = CurvilinearOptions.DefaultNominalBeadWidth,
        int PatchSize = PatchAnomalyOptions.DefaultPatchSize,
        float GridPitchMm = CalibrationOptions.DefaultGridPitchMm,
        float LensK1 = CalibrationOptions.DefaultLensK1,
        MetrologyMeasurementMode MetrologyMode = MetrologyMeasurementMode.Auto,
        MetrologyPolarityMode PolarityMode = MetrologyPolarityMode.Any,
        int CaliperRakeCount = MetrologyOptions.DefaultCaliperRakeCount,
        float CaliperProjectionWidth = MetrologyOptions.DefaultCaliperProjectionWidth,
        int GoldenMinDefectAreaPixels = GoldenVariationOptions.DefaultMinimumDefectAreaPixels,
        int BlobMinimumAreaPixels = BlobRleOptions.DefaultMinimumAreaPixels,
        float FewShotOodSigmaMultiplier = FewShotSuiteOptions.DefaultOodSigmaMultiplier,
        int FewShotMaxSubClusters = FewShotSuiteOptions.DefaultMaxSubClusters,
        float PadimRegularizationLambda = PatchAnomalyOptions.DefaultRegularizationLambda,
        float CornerApexIgnoreMargin = CornerAlignmentOptions.DefaultApexIgnoreMargin,
        float CornerRansacThresholdPixels = CornerAlignmentOptions.DefaultRansacThresholdPixels,
        float CornerOrthogonalityToleranceDeg = CornerAlignmentOptions.DefaultOrthogonalityToleranceDeg,
        float AutoTeachMinGeometricScore = AutoTeachingOptions.DefaultMinGeometricScore,
        float AutoTeachMinAppearanceCosine = AutoTeachingOptions.DefaultMinAppearanceCosine,
        float AutoTeachMaxMahalanobis = AutoTeachingOptions.DefaultMaxMahalanobis,
        int ShadingTargetBaseGray = ShadingReconOptions.DefaultTargetBaseGray,
        float AnisotropicDiffusionKappa = ShadingReconOptions.DefaultAnisotropicDiffusionKappa,
        int ReconstructionMarkerRadius = ShadingReconOptions.DefaultReconstructionMarkerRadius,
        float CompositeExpectedSpanPx = AdvancedMetrologyOptions.DefaultCompositeExpectedSpanPx,
        int EllipseSpokeCount = AdvancedMetrologyOptions.DefaultEllipseSpokeCount,
        float EllipseMaxRmsErrorPx = AdvancedMetrologyOptions.DefaultEllipseMaxRmsErrorPx,
        int WatershedThresholdIntensity = AdvancedBlobOptions.DefaultThresholdIntensity,
        float WatershedMinPeakDistancePx = AdvancedBlobOptions.DefaultMinPeakDistancePx,
        int WatershedMinAreaPixels = AdvancedBlobOptions.DefaultMinimumAreaPixels,
        float DataMatrixMinIsoContrast = DataMatrixOptions.DefaultMinIsoContrast,
        int DataMatrixMaxEccErrors = DataMatrixOptions.DefaultMaxEccErrors,
        float FftNotchOrGaborSigma = FrequencyFilterOptions.DefaultNotchOrGaborSigma,
        float FftPeakThresholdRatio = FrequencyFilterOptions.DefaultPeakThresholdRatio,
        float HomomorphicGammaHigh = FrequencyFilterOptions.DefaultHomomorphicGammaHigh,
        int BarcodeScanlineCount = Barcode1DOptions.DefaultScanlineCount,
        float BarcodeMinSymbolContrast = Barcode1DOptions.DefaultMinSymbolContrast,
        float PhotometricSlantAngleDeg = PhotometricStereoOptions.DefaultSlantAngleDeg,
        float PhotometricCurvatureThreshold = PhotometricStereoOptions.DefaultCurvatureThreshold,
        int PhotometricMaxDefectPixels = PhotometricStereoOptions.DefaultMaxDefectPixels,
        float GdtAngleToleranceDeg = GeometricTolerancingOptions.DefaultAngleToleranceDeg,
        float GdtConcentricityLimitPx = GeometricTolerancingOptions.DefaultConcentricityLimitPx,
        float GdtDistanceTolerancePx = GeometricTolerancingOptions.DefaultDistanceTolerancePx,
        float GdtGradientThreshold = GeometricTolerancingOptions.DefaultGradientThreshold,
        OcrAllowedCharacterSet OcrAllowedCharacters = DefaultOcrAllowedCharacters,
        float PpOcrMinConfidence = DefaultPpOcrMinConfidence,
        float PpOcrMinCharacterConfidence = DefaultPpOcrMinCharacterConfidence,
        PpOcrLanguage PpOcrLanguageMode = DefaultPpOcrLanguageMode,
        PpOcrPolarityMode PpOcrPolarity = DefaultPpOcrPolarity,
        PpOcrPipelineMode PpOcrPipeline = DefaultPpOcrPipeline,
        PpOcrSpeedPreset PpOcrPreset = DefaultPpOcrPreset,
        bool IsInteractivePreview = DefaultIsInteractivePreview,
        float QrCodeMinIsoContrast = QrCodeOptions.DefaultMinIsoContrast,
        int QrCodeMaxEccErrors = QrCodeOptions.DefaultMaxEccErrors)
    {
        public InspectionSuiteParameters ToStructuredParameters() => new(
            GaussianSigma,
            MinimumContrast,
            ThresholdIntensity,
            MorphologyRadius,
            VariationStdMultiplier,
            VariationEdgeMultiplier,
            NominalBeadWidth,
            PatchSize,
            GridPitchMm,
            LensK1,
            MetrologyMode,
            PolarityMode,
            CaliperRakeCount,
            CaliperProjectionWidth,
            GoldenMinDefectAreaPixels,
            BlobMinimumAreaPixels,
            FewShotOodSigmaMultiplier,
            FewShotMaxSubClusters,
            PadimRegularizationLambda,
            CornerApexIgnoreMargin,
            CornerRansacThresholdPixels,
            CornerOrthogonalityToleranceDeg,
            AutoTeachMinGeometricScore,
            AutoTeachMinAppearanceCosine,
            AutoTeachMaxMahalanobis,
            ShadingTargetBaseGray,
            AnisotropicDiffusionKappa,
            ReconstructionMarkerRadius,
            CompositeExpectedSpanPx,
            EllipseSpokeCount,
            EllipseMaxRmsErrorPx,
            WatershedThresholdIntensity,
            WatershedMinPeakDistancePx,
            WatershedMinAreaPixels,
            DataMatrixMinIsoContrast,
            DataMatrixMaxEccErrors,
            FftNotchOrGaborSigma,
            FftPeakThresholdRatio,
            HomomorphicGammaHigh,
            BarcodeScanlineCount,
            BarcodeMinSymbolContrast,
            PhotometricSlantAngleDeg,
            PhotometricCurvatureThreshold,
            PhotometricMaxDefectPixels,
            GdtAngleToleranceDeg,
            GdtConcentricityLimitPx,
            GdtDistanceTolerancePx,
            GdtGradientThreshold,
            OcrAllowedCharacters,
            PpOcrMinConfidence,
            PpOcrMinCharacterConfidence,
            PpOcrLanguageMode,
            PpOcrPolarity,
            PpOcrPipeline,
            PpOcrPreset,
            IsInteractivePreview,
            QrCodeMinIsoContrast,
            QrCodeMaxEccErrors);
    }
}
