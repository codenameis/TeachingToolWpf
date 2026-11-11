using System.Buffers;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Numerics;
using VisionAlgorithm.OpenSrc.AutoTeaching;
using VisionAlgorithm.OpenSrc.BlobRle;
using VisionAlgorithm.OpenSrc.Calibration;
using VisionAlgorithm.OpenSrc.Common;
using VisionAlgorithm.OpenSrc.CornerAlignment.Domain;
using VisionAlgorithm.OpenSrc.CornerAlignment.EdgeRake;
using VisionAlgorithm.OpenSrc.CornerAlignment.LineAndCorner;
using VisionAlgorithm.OpenSrc.CornerAlignment.SyntheticCornerTestGen;
using VisionAlgorithm.OpenSrc.Curvilinear;
using VisionAlgorithm.OpenSrc.DataCode2D;
using VisionAlgorithm.OpenSrc.EdgeFewShot.Engine;
using VisionAlgorithm.OpenSrc.GoldenVariation;
using VisionAlgorithm.OpenSrc.IndustrialOcrOcv;
using VisionAlgorithm.OpenSrc.Metrology;
using VisionAlgorithm.OpenSrc.PatchAnomaly;
using VisionAlgorithm.OpenSrc.PhotometricStereo;
using VisionAlgorithm.OpenSrc.Preprocessing;
using VisionAlgorithm.OpenSrc.Synthetic8kTestGen;
using VisionGeometry;
using VisionImaging.Abstractions;
using VisionOcr.PpOcr;
using FittedLine2D = VisionAlgorithm.OpenSrc.Metrology.FittedLine2D;
using SubpixelEdgePoint = VisionAlgorithm.OpenSrc.CornerAlignment.EdgeRake.SubpixelEdgePoint;

namespace TeachingToolWpf.Imaging.Matching;

/// <summary>
/// 상용 정밀 검사 스위트 20종 서브 알고리즘 구분.
/// </summary>
public enum InspectionSuiteAlgorithmKind
{
    Metrology = 0,
    GoldenVariation = 1,
    BlobRle = 2,
    Curvilinear = 3,
    FewShot = 4,
    PatchAnomaly = 5,
    Calibration = 6,
    CornerAlignment = 7,
    IndustrialOcrOcv = 8,
    LightweightOcr = 9,
    AutoTeaching = 10,
    CpuDeepOcr = 11,
    PreprocessingShadingAndRecon = 12,
    AdvancedMetrologyEllipse = 13,
    AdvancedBlobWatershed = 14,
    DataMatrix2DReader = 15,
    PreprocessingFftAndGabor = 16,
    Barcode1DReader = 17,
    PhotometricStereo = 18,
    GeometricTolerancing = 19,
    PpOcrOnnx = 20
}

/// <summary>
/// 서브픽셀 캘리퍼·기하 계측(Metrology) 세부 동작 모드.
/// </summary>
public enum MetrologyMeasurementMode
{
    Auto = 0,
    EdgePairWidth = 1,
    RakeLineFit = 2,
    SpokeCircleFit = 3
}

/// <summary>
/// 캘리퍼 에지 검출 극성(Polarity) 조건.
/// </summary>
public enum MetrologyPolarityMode
{
    Any = 0,
    BrightStripe = 1,
    DarkSlot = 2,
    DarkToLight = 3,
    LightToDark = 4
}

/// <summary>
/// 검사 스위트 실행 파라미터.
/// </summary>
public sealed record InspectionSuiteParameters(
    float GaussianSigma = 1.2f,
    float MinimumContrast = 18.0f,
    int ThresholdIntensity = 128,
    int MorphologyRadius = 1,
    float VariationStdMultiplier = 3.0f,
    float VariationEdgeMultiplier = 0.5f,
    float NominalBeadWidth = 14.0f,
    int PatchSize = 8,
    float GridPitchMm = 10.0f,
    float LensK1 = -0.05f,
    MetrologyMeasurementMode MetrologyMode = MetrologyMeasurementMode.Auto,
    MetrologyPolarityMode PolarityMode = MetrologyPolarityMode.Any,
    int CaliperRakeCount = 15,
    float CaliperProjectionWidth = 11.0f,
    int GoldenMinDefectAreaPixels = 6,
    int BlobMinimumAreaPixels = 8,
    float FewShotOodSigmaMultiplier = 3.5f,
    int FewShotMaxSubClusters = 2,
    float PadimRegularizationLambda = 0.01f,
    float CornerApexIgnoreMargin = 36.0f,
    float CornerRansacThresholdPixels = 0.55f,
    float CornerOrthogonalityToleranceDeg = 5.0f,
    float AutoTeachMinGeometricScore = 0.45f,
    float AutoTeachMinAppearanceCosine = 0.35f,
    float AutoTeachMaxMahalanobis = 35.0f,
    int ShadingTargetBaseGray = 128,
    float AnisotropicDiffusionKappa = 18.0f,
    int ReconstructionMarkerRadius = 2,
    float CompositeExpectedSpanPx = 28.0f,
    int EllipseSpokeCount = 24,
    float EllipseMaxRmsErrorPx = 1.8f,
    int WatershedThresholdIntensity = 128,
    float WatershedMinPeakDistancePx = 14.0f,
    int WatershedMinAreaPixels = 12,
    float DataMatrixMinIsoContrast = 0.20f,
    int DataMatrixMaxEccErrors = 8,
    float FftNotchOrGaborSigma = 2.5f,
    float FftPeakThresholdRatio = 4.2f,
    float HomomorphicGammaHigh = 1.85f,
    int BarcodeScanlineCount = 9,
    float BarcodeMinSymbolContrast = 0.20f,
    float PhotometricSlantAngleDeg = 45.0f,
    float PhotometricCurvatureThreshold = 0.08f,
    int PhotometricMaxDefectPixels = 6,
    float GdtAngleToleranceDeg = 3.0f,
    float GdtConcentricityLimitPx = 2.5f,
    float GdtDistanceTolerancePx = 4.0f,
    float GdtGradientThreshold = 18.0f,
    OcrAllowedCharacterSet OcrAllowedCharacters = InspectionSuiteParameters.DefaultOcrAllowedCharacters,
    float PpOcrMinConfidence = InspectionSuiteParameters.DefaultPpOcrMinConfidence,
    float PpOcrMinCharacterConfidence = InspectionSuiteParameters.DefaultPpOcrMinCharacterConfidence,
    PpOcrLanguage PpOcrLanguageMode = InspectionSuiteParameters.DefaultPpOcrLanguageMode,
    PpOcrPolarityMode PpOcrPolarity = InspectionSuiteParameters.DefaultPpOcrPolarity,
    PpOcrPipelineMode PpOcrPipeline = InspectionSuiteParameters.DefaultPpOcrPipeline,
    PpOcrSpeedPreset PpOcrPreset = InspectionSuiteParameters.DefaultPpOcrPreset,
    bool IsInteractivePreview = InspectionSuiteParameters.DefaultIsInteractivePreview)
{
    public const OcrAllowedCharacterSet DefaultOcrAllowedCharacters = PpOcrRunOptions.DefaultAllowedCharacterSet;
    public const float DefaultPpOcrMinConfidence = PpOcrRunOptions.DefaultMinAcceptConfidence;
    public const float DefaultPpOcrMinCharacterConfidence = PpOcrRunOptions.DefaultMinCharacterConfidence;
    public const PpOcrLanguage DefaultPpOcrLanguageMode = PpOcrRunOptions.DefaultLanguage;
    public const PpOcrPolarityMode DefaultPpOcrPolarity = PpOcrRunOptions.DefaultPolarityMode;
    public const PpOcrPipelineMode DefaultPpOcrPipeline = PpOcrRunOptions.DefaultPipelineMode;
    public const PpOcrSpeedPreset DefaultPpOcrPreset = PpOcrSpeedPreset.UniversalAuto;
    public const bool DefaultIsInteractivePreview = PpOcrRunOptions.DefaultFastInteractivePreview;
}

/// <summary>
/// 캔버스 ROI 입력 정보.
/// </summary>
public sealed record InspectionSuiteRoiInput(
    Guid RoiId,
    string Name,
    PointD Center,
    double Width,
    double Height,
    double AngleRadians,
    ImmutableArray<PointD> Outline,
    int ClassLabel = 0);

/// <summary>
/// 개별 ROI 검사 결과 및 캔버스 시각화 기하 정보.
/// </summary>
public sealed record InspectionSuiteItemResult(
    Guid RoiId,
    string Name,
    bool IsPass,
    string BadgeLabel,
    string DetailLine,
    PointD ReferencePoint,
    double AngleRadians,
    ImmutableArray<PointD> Bounds,
    ImmutableArray<ImmutableArray<PointD>> AdditionalContours,
    ImmutableArray<PointD> PrimaryPoints,
    ImmutableArray<PointD> SecondaryPoints,
    ImmutableArray<PointD> DefectOrOutlierPoints,
    double ElapsedMicroseconds,
    ImmutableArray<ImmutableArray<PointD>> GuideContours = default,
    OcrDiagnosticSnapshot? OcrDiagnostic = null);

/// <summary>
/// 검사 스위트 실행 종합 결과.
/// </summary>
public sealed record InspectionSuiteExecutionOutput(
    InspectionSuiteAlgorithmKind Algorithm,
    string SummaryTitle,
    string DetailedReport,
    ImmutableArray<InspectionSuiteItemResult> Items,
    double TotalMilliseconds);

public interface IInspectionSuiteService
{
    bool IsPpOcrWired => false;

    string PpOcrStatusSummary => "PP-OCR 미연결";

    Task<InspectionSuiteExecutionOutput> ExecuteRoiInspectionAsync(
        ImageReference image,
        bool live,
        InspectionSuiteAlgorithmKind algorithm,
        IReadOnlyList<InspectionSuiteRoiInput> targetRois,
        IReadOnlyList<InspectionSuiteRoiInput> referenceRois,
        InspectionSuiteParameters parameters,
        CancellationToken token);

    Task<InspectionSuiteExecutionOutput> RunMaster8kSuiteAsync(CancellationToken token);
}

/// <summary>
/// <c>VisionAlgorithm.OpenSrc.InspectionSuite</c> 알고리즘을 캔버스 ROI 및 8K 마스터 검증과 연결하는 서비스.
/// </summary>
public sealed class InspectionSuiteService(
    IImageLoader loader,
    Func<VideoFrame?> retainFrame,
    IPpOcrInferenceService? ppOcrService = null) : IInspectionSuiteService
{
    public bool IsPpOcrWired => ppOcrService is not null;

    public string PpOcrStatusSummary => ppOcrService?.StatusMessage ?? "PP-OCR 미연결";
    public static InspectionSuiteAlgorithmKind NormalizeAlgorithmKind(InspectionSuiteAlgorithmKind kind) => kind switch
    {
        InspectionSuiteAlgorithmKind.IndustrialOcrOcv
            or InspectionSuiteAlgorithmKind.LightweightOcr
            or InspectionSuiteAlgorithmKind.CpuDeepOcr => InspectionSuiteAlgorithmKind.PpOcrOnnx,
        _ => kind
    };

    public Task<InspectionSuiteExecutionOutput> ExecuteRoiInspectionAsync(
        ImageReference image,
        bool live,
        InspectionSuiteAlgorithmKind algorithm,
        IReadOnlyList<InspectionSuiteRoiInput> targetRois,
        IReadOnlyList<InspectionSuiteRoiInput> referenceRois,
        InspectionSuiteParameters parameters,
        CancellationToken token)
        => Task.Run(async () =>
        {
            ArgumentNullException.ThrowIfNull(targetRois);
            if (targetRois.Count == 0)
            {
                throw new ArgumentException("검사할 ROI를 먼저 선택하거나 추가해 주세요.");
            }

            InspectionSuiteAlgorithmKind normalizedAlgorithm = NormalizeAlgorithmKind(algorithm);
            using var reader = await MatchingImageReader.OpenAsync(image, live, loader, retainFrame, token).ConfigureAwait(false);
            reader.PreferDirectGray = true;
            using var workspace = new InspectionWorkspace();

            var sw = Stopwatch.StartNew();
            var items = normalizedAlgorithm switch
            {
                InspectionSuiteAlgorithmKind.Metrology => RunMetrology(reader, image.Width, image.Height, targetRois, parameters, workspace, token),
                InspectionSuiteAlgorithmKind.GoldenVariation => RunGoldenVariation(reader, image.Width, image.Height, targetRois, referenceRois, parameters, workspace, token),
                InspectionSuiteAlgorithmKind.BlobRle => RunBlobRle(reader, image.Width, image.Height, targetRois, parameters, workspace, token),
                InspectionSuiteAlgorithmKind.Curvilinear => RunCurvilinear(reader, image.Width, image.Height, targetRois, parameters, workspace, token),
                InspectionSuiteAlgorithmKind.FewShot => RunFewShot(reader, image.Width, image.Height, targetRois, referenceRois, parameters, workspace, token),
                InspectionSuiteAlgorithmKind.PatchAnomaly => RunPatchAnomaly(reader, image.Width, image.Height, targetRois, referenceRois, parameters, workspace, token),
                InspectionSuiteAlgorithmKind.Calibration => RunCalibration(targetRois, parameters, image.Width, image.Height),
                InspectionSuiteAlgorithmKind.CornerAlignment => RunCornerAlignment(reader, image.Width, image.Height, targetRois, parameters, token),
                InspectionSuiteAlgorithmKind.AutoTeaching => RunAutoTeaching(reader, image.Width, image.Height, targetRois, referenceRois, parameters, workspace, token),
                InspectionSuiteAlgorithmKind.PreprocessingShadingAndRecon => RunPreprocessingShadingAndRecon(reader, image.Width, image.Height, targetRois, parameters, workspace, token),
                InspectionSuiteAlgorithmKind.AdvancedMetrologyEllipse => RunAdvancedMetrologyEllipse(reader, image.Width, image.Height, targetRois, parameters, workspace, token),
                InspectionSuiteAlgorithmKind.AdvancedBlobWatershed => RunAdvancedBlobWatershed(reader, image.Width, image.Height, targetRois, parameters, workspace, token),
                InspectionSuiteAlgorithmKind.DataMatrix2DReader => RunDataMatrix2DReader(reader, image.Width, image.Height, targetRois, parameters, token),
                InspectionSuiteAlgorithmKind.PreprocessingFftAndGabor => RunPreprocessingFftAndGabor(reader, image.Width, image.Height, targetRois, parameters, workspace, token),
                InspectionSuiteAlgorithmKind.Barcode1DReader => RunBarcode1DReader(reader, image.Width, image.Height, targetRois, parameters, token),
                InspectionSuiteAlgorithmKind.PhotometricStereo => RunPhotometricStereo(reader, image.Width, image.Height, targetRois, parameters, token),
                InspectionSuiteAlgorithmKind.GeometricTolerancing => RunGeometricTolerancing(reader, image.Width, image.Height, targetRois, parameters, token),
                InspectionSuiteAlgorithmKind.PpOcrOnnx => RunPpOcrOnnx(reader, image.Width, image.Height, targetRois, parameters, ppOcrService, token),
                _ => throw new ArgumentOutOfRangeException(nameof(algorithm))
            };
            sw.Stop();

            int passCount = items.Count(item => item.IsPass);
            string algoName = GetAlgorithmShortName(normalizedAlgorithm);
            string summaryTitle = $"{algoName} 완료 · 합격 {passCount}/{items.Length}개 · {sw.Elapsed.TotalMilliseconds:F2} ms";
            string detailedReport = string.Join(Environment.NewLine, items.Select((item, idx) => $"[{idx + 1}] {item.Name}: {item.DetailLine} ({item.ElapsedMicroseconds:F1} µs)"));

            return new InspectionSuiteExecutionOutput(normalizedAlgorithm, summaryTitle, detailedReport, items, sw.Elapsed.TotalMilliseconds);
        }, token);

    public Task<InspectionSuiteExecutionOutput> RunMaster8kSuiteAsync(CancellationToken token)
        => Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            InspectionSuite8kVerificationReport report = InspectionSuite8kTestRunner.RunAllVerifications();

            var markers = ImmutableArray.CreateBuilder<InspectionSuiteItemResult>(7);
            markers.Add(CreateZoneMarker(
                "1. Metrology (1D Caliper / RANSAC+IRLS / Taubin)",
                new PointD(1400, 1400),
                420,
                320,
                report.CaliperWidthErrorPixels < 0.08f && report.CircleRadiusErrorPixels < 0.08f,
                $"CalErr={report.CaliperWidthErrorPixels:F4}px · CirErr={report.CircleRadiusErrorPixels:F4}px",
                $"1D 폭 오차 {report.CaliperWidthErrorPixels:F4}px, 직선 각도 오차 {report.LineAngleErrorDegrees:F4}°, 원 반지름 오차 {report.CircleRadiusErrorPixels:F4}px (중심 오차 {report.CircleCenterErrorPixels:F4}px)"));

            markers.Add(CreateZoneMarker(
                "2. Golden Variation (Welford Mean/Std + Sobel Tolerance)",
                new PointD(3400, 1400),
                420,
                320,
                report.GoldenNormalFalseDefectPixels == 0 && report.GoldenDefectiveDetectedPixels > 20,
                $"Normal={report.GoldenNormalFalseDefectPixels}px · Defect={report.GoldenDefectiveDetectedPixels}px",
                $"정상 다이 오검출 {report.GoldenNormalFalseDefectPixels}px, 불량 다이 검출 {report.GoldenDefectiveDetectedPixels}px"));

            markers.Add(CreateZoneMarker(
                "3. RLE Blob & Interval Morphology",
                new PointD(5256, 2256),
                512,
                512,
                report.RleCleanedBlobCount == 1 && report.RleBlobCentroidErrorPixels < 0.25f,
                $"Blobs={report.RleCleanedBlobCount} · CentroidErr={report.RleBlobCentroidErrorPixels:F4}px",
                $"오프닝 노이즈 제거 후 블롭 {report.RleCleanedBlobCount}개, 무게중심 오차 {report.RleBlobCentroidErrorPixels:F4}px"));

            markers.Add(CreateZoneMarker(
                "4. Curvilinear Steger Hessian Ridge & Bead Continuity",
                new PointD(2600, 4200),
                1200,
                240,
                report.StegerCenterlineRmsErrorPixels < 0.05f && report.ContinuousBeadPassed && report.GappedBeadDetectedGapCount > 0,
                $"StegerRMS={report.StegerCenterlineRmsErrorPixels:F4}px · Gaps={report.GappedBeadDetectedGapCount}",
                $"Steger 중심선 RMS 오차 {report.StegerCenterlineRmsErrorPixels:F4}px, 정상 비드 통과={report.ContinuousBeadPassed}, 단선 구간 검출={report.GappedBeadDetectedGapCount}개"));

            markers.Add(CreateZoneMarker(
                "5. Few-Shot (GeM + SimpleShot + Ledoit-Wolf + OOD)",
                new PointD(5400, 4200),
                420,
                320,
                report.FewShotClassifiedKnownClassesCorrectly && report.FewShotRejectedOodAnomaly,
                $"Known={report.FewShotClassifiedKnownClassesCorrectly} · OOD={report.FewShotRejectedOodAnomaly}",
                $"기등록 클래스 정분류={report.FewShotClassifiedKnownClassesCorrectly}, 미등록 이상치(OOD) 기각={report.FewShotRejectedOodAnomaly}"));

            markers.Add(CreateZoneMarker(
                "6. PaDiM Patch Anomaly Heatmap Detector",
                new PointD(3800, 1400),
                420,
                320,
                report.PadimAcceptedNormalDie && report.PadimDetectedDefectiveDie && report.PadimLocalizationErrorPixels <= 16.0f,
                $"NormalOK={report.PadimAcceptedNormalDie} · LocErr={report.PadimLocalizationErrorPixels:F2}px",
                $"정상 다이 통과={report.PadimAcceptedNormalDie}, 불량 다이 이상 탐지={report.PadimDetectedDefectiveDie}, 피크 위치 오차={report.PadimLocalizationErrorPixels:F2}px"));

            markers.Add(CreateZoneMarker(
                "7. 9-Point Hartley Homography & Brown-Conrady Lens",
                new PointD(4096, 4096),
                640,
                640,
                report.HomographyRmsErrorMm < 1e-3 && report.LensUndistortRoundTripErrorPixels < 1e-3f,
                $"HomographyRMS={report.HomographyRmsErrorMm:E2}mm · LensErr={report.LensUndistortRoundTripErrorPixels:E2}px",
                $"9-Point 호모그래피 재투영 RMS={report.HomographyRmsErrorMm:E3}mm, Brown-Conrady 왜곡 역보정 왕복 오차={report.LensUndistortRoundTripErrorPixels:E3}px"));

            var cornerReport = CornerAlignment8kTestSuite.RunAllVerifications();
            markers.Add(CreateZoneMarker(
                "8. 2-Rake Virtual Corner & 4-Corner Diagonal / UVW Alignment",
                new PointD(2000, 6200),
                480,
                360,
                cornerReport.MaxPositionErrorPx <= 0.02f && cornerReport.DiagonalCenterErrorPx <= 0.015f,
                $"CornerErr={cornerReport.MaxPositionErrorPx:F4}px · Diag8K={cornerReport.DiagonalCenterErrorPx:F4}px",
                $"20자세 가상 코너 최대 오차={cornerReport.MaxPositionErrorPx:F4}px, 8K 대각 정합 오차={cornerReport.DiagonalCenterErrorPx:F4}px, UVW 왕복={cornerReport.UvwRoundTripErrorMm:E2}mm"));

            var unifiedReport = UnifiedNewSubsystems8kRunner.RunAllVerifications();
            foreach (var c in unifiedReport.Cases)
            {
                markers.Add(CreateZoneMarker(
                    $"[{c.SuiteName}] {c.CaseName}",
                    new PointD(4096, 6200),
                    420,
                    240,
                    c.Passed,
                    $"{(c.Passed ? "PASS" : "FAIL")} · {c.ElapsedMilliseconds:F1}ms",
                    c.Details));
            }

            var builtItems = markers.MoveToImmutable();
            int passCount = builtItems.Count(item => item.IsPass);
            double totalMs = report.TotalAlgorithmExecutionMilliseconds + unifiedReport.TotalElapsedMilliseconds;
            string summaryTitle = $"8K 전수 통합 검증 ({builtItems.Length}건: 7종 스위트 + 코너정합 + OCR/OCV·경량OCR·자동티칭 19종) · PASS {passCount}/{builtItems.Length} · {totalMs:F1}ms";
            string detailedReport = string.Join(Environment.NewLine, builtItems.Select(item => $"• [{(item.IsPass ? "PASS" : "FAIL")}] {item.Name}: {item.DetailLine}"));

            return new InspectionSuiteExecutionOutput(
                InspectionSuiteAlgorithmKind.Metrology,
                summaryTitle,
                detailedReport,
                builtItems,
                totalMs);
        }, token);

    private static ImmutableArray<InspectionSuiteItemResult> RunMetrology(
        MatchingImageReader reader,
        int imageWidth,
        int imageHeight,
        IReadOnlyList<InspectionSuiteRoiInput> targets,
        InspectionSuiteParameters parameters,
        InspectionWorkspace workspace,
        CancellationToken token)
    {
        var results = ImmutableArray.CreateBuilder<InspectionSuiteItemResult>(targets.Count);
        int projStrips = Math.Clamp((int)Math.Round(parameters.CaliperProjectionWidth), 3, 45);
        var spec = new CaliperMeasurementSpec(
            gaussianSigma: Math.Clamp(parameters.GaussianSigma, 0.5f, 8.0f),
            minimumContrast: Math.Clamp(parameters.MinimumContrast, 2.0f, 200.0f),
            projectionStripCount: projStrips);

        var (firstPolarity, secondPolarity, singlePolarity) = ResolvePolarities(parameters.PolarityMode);
        int rakeStations = Math.Clamp(parameters.CaliperRakeCount, 5, 48);
        int spokeCount = Math.Clamp(parameters.CaliperRakeCount, 8, 48);

        for (int i = 0; i < targets.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var target = targets[i];
            long startTick = Stopwatch.GetTimestamp();

            var (crop, localCenter, offsetX, offsetY) = ReadLocalCrop(reader, imageWidth, imageHeight, target, marginPixels: 32, token);
            var localPose = new FixturePose2D(localCenter.X, localCenter.Y, (float)target.AngleRadians, 1.0f);

            float scanLen = MathF.Max(24.0f, (float)target.Width * 0.90f);
            float projWidth = Math.Clamp(parameters.CaliperProjectionWidth, 3.0f, MathF.Max(5.0f, (float)target.Height * 0.85f));
            float halfHeight = MathF.Max(12.0f, (float)target.Height * 0.42f);
            float nominalRadius = MathF.Max(8.0f, MathF.Min((float)target.Width, (float)target.Height) * 0.40f);
            float radialScanLen = MathF.Max(16.0f, nominalRadius * 0.78f);

            MetrologyMeasurementMode effectiveMode = parameters.MetrologyMode;
            bool isCircleHint = target.Name.Contains("원", StringComparison.OrdinalIgnoreCase)
                || target.Name.Contains("Circle", StringComparison.OrdinalIgnoreCase)
                || target.Name.Contains("패드", StringComparison.OrdinalIgnoreCase)
                || target.Name.Contains("홀", StringComparison.OrdinalIgnoreCase);

            bool runPair = effectiveMode is MetrologyMeasurementMode.Auto or MetrologyMeasurementMode.EdgePairWidth;
            bool runRake = effectiveMode is MetrologyMeasurementMode.Auto or MetrologyMeasurementMode.RakeLineFit;
            bool runCircle = effectiveMode is MetrologyMeasurementMode.Auto or MetrologyMeasurementMode.SpokeCircleFit;

            CaliperPairResult pair = default;
            if (runPair)
            {
                pair = SubpixelCaliper1D.MeasureEdgePairWidth(
                    crop.Pixels.Span,
                    crop.Width,
                    crop.Height,
                    crop.Stride,
                    localPose,
                    scanLengthPixels: scanLen,
                    projectionWidthPixels: projWidth,
                    firstPolarity: firstPolarity,
                    secondPolarity: secondPolarity,
                    spec: spec,
                    workspace: workspace);
            }

            List<Vector2> rakeHits = [];
            FittedLine2D fittedLine = default;
            if (runRake)
            {
                (rakeHits, fittedLine) = MeasureBestRakeLine(
                    crop.Pixels.Span,
                    crop.Width,
                    crop.Height,
                    crop.Stride,
                    localPose,
                    scanLen,
                    halfHeight,
                    rakeStations,
                    singlePolarity,
                    spec,
                    workspace);
            }

            FittedCircle2D circle = default;
            List<Vector2> spokeHits = [];
            if (runCircle)
            {
                circle = TaubinCircleFitter.MeasureSpokeCircle(
                    crop.Pixels.Span,
                    crop.Width,
                    crop.Height,
                    crop.Stride,
                    localCenter,
                    nominalRadius: nominalRadius,
                    radialScanLength: radialScanLen,
                    spokeCount: spokeCount,
                    radialPolarity: singlePolarity == CaliperEdgePolarity.DarkToLight ? CaliperEdgePolarity.DarkToLight :
                        singlePolarity == CaliperEdgePolarity.LightToDark ? CaliperEdgePolarity.LightToDark : CaliperEdgePolarity.Any,
                    caliperSpec: spec,
                    workspace: workspace);

                spokeHits = CollectSpokeEdgePoints(
                    crop.Pixels.Span,
                    crop.Width,
                    crop.Height,
                    crop.Stride,
                    localCenter,
                    nominalRadius,
                    radialScanLen,
                    spokeCount,
                    singlePolarity,
                    spec,
                    workspace);
            }

            var primaryPts = ImmutableArray.CreateBuilder<PointD>();
            var secondaryPts = ImmutableArray.CreateBuilder<PointD>();
            var outlierPts = ImmutableArray.CreateBuilder<PointD>();
            var contours = ImmutableArray.CreateBuilder<ImmutableArray<PointD>>();

            MetrologyMeasurementMode guideMode = effectiveMode;
            if (effectiveMode == MetrologyMeasurementMode.Auto)
            {
                bool circleDominant = isCircleHint || (circle.IsValid && circle.RmsRadialErrorPixels < 1.2f && !pair.IsFound);
                guideMode = circleDominant ? MetrologyMeasurementMode.SpokeCircleFit : MetrologyMeasurementMode.EdgePairWidth;
            }

            ImmutableArray<ImmutableArray<PointD>> guideContours = BuildGeometricGuidesForRoi(
                target,
                InspectionSuiteAlgorithmKind.Metrology,
                parameters with { MetrologyMode = guideMode });

            double cos = Math.Cos(target.AngleRadians);
            double sin = Math.Sin(target.AngleRadians);
            PointD normalDir = new(-sin, cos);
            double tickHalf = Math.Clamp(projWidth * 0.5, 4.0, 22.0);

            if (runPair && pair.IsFound && (effectiveMode != MetrologyMeasurementMode.Auto || !isCircleHint))
            {
                PointD p1 = new(pair.FirstEdge.WorldPoint.X + offsetX, pair.FirstEdge.WorldPoint.Y + offsetY);
                PointD p2 = new(pair.SecondEdge.WorldPoint.X + offsetX, pair.SecondEdge.WorldPoint.Y + offsetY);
                primaryPts.Add(p1);
                primaryPts.Add(p2);
                contours.Add([p1, p2]);
                contours.Add([
                    new PointD(p1.X - (normalDir.X * tickHalf), p1.Y - (normalDir.Y * tickHalf)),
                    new PointD(p1.X + (normalDir.X * tickHalf), p1.Y + (normalDir.Y * tickHalf))
                ]);
                contours.Add([
                    new PointD(p2.X - (normalDir.X * tickHalf), p2.Y - (normalDir.Y * tickHalf)),
                    new PointD(p2.X + (normalDir.X * tickHalf), p2.Y + (normalDir.Y * tickHalf))
                ]);
            }

            string lineSummary = "직선 미검출";
            double lineAngleDeg = 0.0;
            if (runRake && rakeHits.Count >= 3 && (effectiveMode != MetrologyMeasurementMode.Auto || !isCircleHint))
            {
                if (fittedLine.IsValid)
                {
                    Vector2 lineStart = fittedLine.Centroid - (fittedLine.Direction * halfHeight);
                    Vector2 lineEnd = fittedLine.Centroid + (fittedLine.Direction * halfHeight);
                    contours.Add([
                        new PointD(lineStart.X + offsetX, lineStart.Y + offsetY),
                        new PointD(lineEnd.X + offsetX, lineEnd.Y + offsetY)
                    ]);
                    lineAngleDeg = Math.Atan2(fittedLine.Direction.Y, fittedLine.Direction.X) * (180.0 / Math.PI);
                    lineSummary = $"직선 {lineAngleDeg:F2}° (인라이어 {fittedLine.InlierCount}/{rakeHits.Count}, RMS {fittedLine.RmsResidualPixels:F3}px)";

                    for (int k = 0; k < rakeHits.Count; k++)
                    {
                        Vector2 pt = rakeHits[k];
                        float dist = MathF.Abs( Vector2.Dot(pt - fittedLine.Centroid, fittedLine.Normal));
                        PointD worldPt = new(pt.X + offsetX, pt.Y + offsetY);
                        if (dist <= 1.5f)
                        {
                            if (effectiveMode == MetrologyMeasurementMode.RakeLineFit)
                            {
                                primaryPts.Add(worldPt);
                            }
                            else
                            {
                                secondaryPts.Add(worldPt);
                            }
                        }
                        else
                        {
                            outlierPts.Add(worldPt);
                        }
                    }
                }
            }

            string circleSummary = "원 미검출";
            if (runCircle && circle.IsValid && (effectiveMode != MetrologyMeasurementMode.Auto || isCircleHint || !pair.IsFound))
            {
                PointD worldCircleCenter = new(circle.Center.X + offsetX, circle.Center.Y + offsetY);
                contours.Add(BuildCirclePolygon(worldCircleCenter, circle.Radius, 48));
                contours.Add([new PointD(worldCircleCenter.X - 6, worldCircleCenter.Y), new PointD(worldCircleCenter.X + 6, worldCircleCenter.Y)]);
                contours.Add([new PointD(worldCircleCenter.X, worldCircleCenter.Y - 6), new PointD(worldCircleCenter.X, worldCircleCenter.Y + 6)]);
                circleSummary = $"Taubin 원 R={circle.Radius:F3}px (Ø={circle.Radius * 2f:F2}px, RMS {circle.RmsRadialErrorPixels:F3}px)";

                for (int k = 0; k < spokeHits.Count; k++)
                {
                    PointD worldSpokePt = new(spokeHits[k].X + offsetX, spokeHits[k].Y + offsetY);
                    if (effectiveMode == MetrologyMeasurementMode.SpokeCircleFit || isCircleHint)
                    {
                        primaryPts.Add(worldSpokePt);
                    }
                    else
                    {
                        secondaryPts.Add(worldSpokePt);
                    }
                }
            }

            double elapsedUs = (Stopwatch.GetTimestamp() - startTick) * 1_000_000.0 / Stopwatch.Frequency;
            string widthText = pair.IsFound ? $"폭={pair.SubpixelWidth:F3}px" : "폭 미검출";

            bool isPass;
            string badge;
            string detail;
            switch (effectiveMode)
            {
                case MetrologyMeasurementMode.EdgePairWidth:
                    isPass = pair.IsFound;
                    badge = pair.IsFound ? $"1D {widthText}" : "1D 폭 미검출";
                    detail = pair.IsFound
                        ? $"1D 캘리퍼 {widthText} (좌에지 대비={pair.FirstEdge.GradientAmplitude:F1}, 우에지 대비={pair.SecondEdge.GradientAmplitude:F1}, 투영폭={projWidth:F0}px)"
                        : "1D 양측 에지 쌍 미검출 — ROI 화살표가 측정할 양쪽 경계를 가로지르도록 배치·회전하거나 최소 대비를 낮추세요.";
                    break;

                case MetrologyMeasurementMode.RakeLineFit:
                    isPass = fittedLine.IsValid && fittedLine.InlierCount >= 3;
                    badge = isPass ? $"레이크 직선 {lineAngleDeg:F2}° (RMS {fittedLine.RmsResidualPixels:F3}px)" : "레이크 직선 미검출";
                    detail = isPass
                        ? $"다중 캘리퍼 레이크({rakeStations}개) {lineSummary}"
                        : $"다중 캘리퍼 레이크 직선 미검출 (검출 에지 {rakeHits.Count}/{rakeStations}개) — ROI 화살표를 에지에 수직으로 맞추세요.";
                    break;

                case MetrologyMeasurementMode.SpokeCircleFit:
                    isPass = circle.IsValid;
                    badge = circle.IsValid ? $"스포크 원 R={circle.Radius:F2}px (Ø={circle.Radius * 2f:F2}px)" : "스포크 원 미검출";
                    detail = circle.IsValid
                        ? $"방사형 스포크({spokeCount}개) {circleSummary}, 중심=({circle.Center.X + offsetX:F2}, {circle.Center.Y + offsetY:F2})"
                        : $"방사형 스포크 원 미검출 — 원형 홀/패드 중심에 정사각형 ROI를 맞추고 반지름이 ROI 크기의 약 35~45%가 되도록 조절하세요.";
                    break;

                default:
                    isPass = pair.IsFound || circle.IsValid || fittedLine.IsValid;
                    if (isCircleHint && circle.IsValid)
                    {
                        badge = $"원 R={circle.Radius:F2}px (Ø={circle.Radius * 2f:F2}px)";
                        detail = $"자동 계측(원형) · {circleSummary} · 1D {widthText}";
                    }
                    else
                    {
                        badge = pair.IsFound ? $"{widthText} · 직선 {(fittedLine.IsValid ? $"{lineAngleDeg:F1}°" : "-")}" : circleSummary;
                        detail = $"1D 캘리퍼 {widthText} · {lineSummary}" + (circle.IsValid ? $" · {circleSummary}" : string.Empty);
                    }
                    break;
            }

            results.Add(new InspectionSuiteItemResult(
                target.RoiId,
                target.Name,
                isPass,
                badge,
                detail,
                target.Center,
                target.AngleRadians,
                target.Outline,
                contours.ToImmutable(),
                primaryPts.ToImmutable(),
                secondaryPts.ToImmutable(),
                outlierPts.ToImmutable(),
                elapsedUs,
                guideContours));
        }

        return results.MoveToImmutable();
    }

    private static (CaliperEdgePolarity First, CaliperEdgePolarity Second, CaliperEdgePolarity Single) ResolvePolarities(MetrologyPolarityMode mode)
        => mode switch
        {
            MetrologyPolarityMode.BrightStripe => (CaliperEdgePolarity.DarkToLight, CaliperEdgePolarity.LightToDark, CaliperEdgePolarity.DarkToLight),
            MetrologyPolarityMode.DarkSlot => (CaliperEdgePolarity.LightToDark, CaliperEdgePolarity.DarkToLight, CaliperEdgePolarity.LightToDark),
            MetrologyPolarityMode.DarkToLight => (CaliperEdgePolarity.DarkToLight, CaliperEdgePolarity.DarkToLight, CaliperEdgePolarity.DarkToLight),
            MetrologyPolarityMode.LightToDark => (CaliperEdgePolarity.LightToDark, CaliperEdgePolarity.LightToDark, CaliperEdgePolarity.LightToDark),
            _ => (CaliperEdgePolarity.Any, CaliperEdgePolarity.Any, CaliperEdgePolarity.Any)
        };

    private static (List<Vector2> Hits, FittedLine2D Line) MeasureBestRakeLine(
        ReadOnlySpan<byte> image,
        int width,
        int height,
        int stride,
        FixturePose2D localPose,
        float scanLen,
        float halfHeight,
        int rakeStations,
        CaliperEdgePolarity desiredPolarity,
        CaliperMeasurementSpec spec,
        InspectionWorkspace workspace)
    {
        if (desiredPolarity != CaliperEdgePolarity.Any)
        {
            var hits = CollectRakeHits(image, width, height, stride, localPose, scanLen, halfHeight, rakeStations, desiredPolarity, spec, workspace);
            var line = hits.Count >= 3
                ? RansacIrlsLineFitter.FitLine(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(hits), 1.5f, workspace)
                : default;
            return (hits, line);
        }

        // When Any polarity is selected on a symmetric stripe, evaluate DarkToLight and LightToDark separately
        // so individual rake teeth do not alternate between the left and right edges of the stripe.
        var risingHits = CollectRakeHits(image, width, height, stride, localPose, scanLen, halfHeight, rakeStations, CaliperEdgePolarity.DarkToLight, spec, workspace);
        var fallingHits = CollectRakeHits(image, width, height, stride, localPose, scanLen, halfHeight, rakeStations, CaliperEdgePolarity.LightToDark, spec, workspace);

        FittedLine2D risingLine = risingHits.Count >= 3
            ? RansacIrlsLineFitter.FitLine(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(risingHits), 1.5f, workspace)
            : default;
        FittedLine2D fallingLine = fallingHits.Count >= 3
            ? RansacIrlsLineFitter.FitLine(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(fallingHits), 1.5f, workspace)
            : default;

        if (risingLine.IsValid && (!fallingLine.IsValid || risingLine.InlierCount > fallingLine.InlierCount ||
            (risingLine.InlierCount == fallingLine.InlierCount && risingLine.RmsResidualPixels <= fallingLine.RmsResidualPixels)))
        {
            return (risingHits, risingLine);
        }

        if (fallingLine.IsValid)
        {
            return (fallingHits, fallingLine);
        }

        return risingHits.Count >= fallingHits.Count ? (risingHits, risingLine) : (fallingHits, fallingLine);
    }

    private static List<Vector2> CollectRakeHits(
        ReadOnlySpan<byte> image,
        int width,
        int height,
        int stride,
        FixturePose2D localPose,
        float scanLen,
        float halfHeight,
        int rakeStations,
        CaliperEdgePolarity polarity,
        CaliperMeasurementSpec spec,
        InspectionWorkspace workspace)
    {
        var hits = new List<Vector2>(rakeStations);
        for (int r = 0; r < rakeStations; r++)
        {
            float t = rakeStations == 1 ? 0f : (-1f + (2f * r / (rakeStations - 1)));
            Vector2 stationCenter = localPose.LocalToWorld(0f, t * halfHeight);
            var toothPose = new FixturePose2D(stationCenter.X, stationCenter.Y, localPose.AngleRad, 1.0f);
            if (SubpixelCaliper1D.TryMeasureSingleEdge(
                image,
                width,
                height,
                stride,
                toothPose,
                scanLengthPixels: scanLen,
                projectionWidthPixels: 5.0f,
                desiredPolarity: polarity,
                spec: spec,
                workspace: workspace,
                out CaliperEdgeHit toothHit))
            {
                hits.Add(toothHit.WorldPoint);
            }
        }
        return hits;
    }

    private static List<Vector2> CollectSpokeEdgePoints(
        ReadOnlySpan<byte> image,
        int width,
        int height,
        int stride,
        Vector2 localCenter,
        float nominalRadius,
        float radialScanLength,
        int spokeCount,
        CaliperEdgePolarity polarity,
        CaliperMeasurementSpec spec,
        InspectionWorkspace workspace)
    {
        var pts = new List<Vector2>(spokeCount);
        for (int s = 0; s < spokeCount; s++)
        {
            float angle = (2.0f * MathF.PI * s) / spokeCount;
            float cos = MathF.Cos(angle);
            float sin = MathF.Sin(angle);
            Vector2 spokeMid = new(localCenter.X + (cos * nominalRadius), localCenter.Y + (sin * nominalRadius));
            var spokePose = new FixturePose2D(spokeMid.X, spokeMid.Y, angle, 1.0f);
            if (SubpixelCaliper1D.TryMeasureSingleEdge(
                image,
                width,
                height,
                stride,
                spokePose,
                scanLengthPixels: radialScanLength,
                projectionWidthPixels: 5.0f,
                desiredPolarity: polarity,
                spec: spec,
                workspace: workspace,
                out CaliperEdgeHit hit))
            {
                pts.Add(hit.WorldPoint);
            }
        }
        return pts;
    }

    /// <summary>
    /// 선택된 ROI 및 현재 알고리즘/파라미터에 대한 캔버스 기하 가이드(스캔 화살표, 투영 스트립, 레이크 보조선, 스포크 원형 링 등)를 생성한다.
    /// </summary>
    public static ImmutableArray<ImmutableArray<PointD>> BuildGeometricGuidesForRoi(
        InspectionSuiteRoiInput target,
        InspectionSuiteAlgorithmKind algorithm,
        InspectionSuiteParameters parameters)
    {
        var guides = ImmutableArray.CreateBuilder<ImmutableArray<PointD>>();
        double cos = Math.Cos(target.AngleRadians);
        double sin = Math.Sin(target.AngleRadians);
        PointD center = target.Center;

        PointD LocalToWorld(double lx, double ly)
            => new(center.X + (cos * lx) - (sin * ly), center.Y + (sin * lx) + (cos * ly));

        switch (algorithm)
        {
            case InspectionSuiteAlgorithmKind.Metrology:
            {
                MetrologyMeasurementMode mode = parameters.MetrologyMode;
                if (mode == MetrologyMeasurementMode.Auto)
                {
                    bool isCircleHint = target.Name.Contains("원", StringComparison.OrdinalIgnoreCase)
                        || target.Name.Contains("Circle", StringComparison.OrdinalIgnoreCase)
                        || target.Name.Contains("패드", StringComparison.OrdinalIgnoreCase)
                        || target.Name.Contains("홀", StringComparison.OrdinalIgnoreCase);
                    mode = isCircleHint ? MetrologyMeasurementMode.SpokeCircleFit : MetrologyMeasurementMode.EdgePairWidth;
                }

                double halfScan = Math.Max(12.0, target.Width * 0.45);
                if (mode == MetrologyMeasurementMode.EdgePairWidth)
                {
                    double halfProj = Math.Clamp(parameters.CaliperProjectionWidth * 0.5, 2.5, Math.Max(4.0, target.Height * 0.42));
                    // Projection box
                    guides.Add([
                        LocalToWorld(-halfScan, -halfProj),
                        LocalToWorld(halfScan, -halfProj),
                        LocalToWorld(halfScan, halfProj),
                        LocalToWorld(-halfScan, halfProj)
                    ]);
                    // Center scan line + arrowhead
                    guides.Add([LocalToWorld(-halfScan, 0), LocalToWorld(halfScan, 0)]);
                    double arrowSize = Math.Clamp(halfScan * 0.14, 4.0, 10.0);
                    guides.Add([LocalToWorld(halfScan - arrowSize, -arrowSize * 0.6), LocalToWorld(halfScan, 0)]);
                    guides.Add([LocalToWorld(halfScan - arrowSize, arrowSize * 0.6), LocalToWorld(halfScan, 0)]);
                }
                else if (mode == MetrologyMeasurementMode.RakeLineFit)
                {
                    int rakeCount = Math.Clamp(parameters.CaliperRakeCount, 5, 32);
                    double halfHeight = Math.Max(12.0, target.Height * 0.42);
                    for (int r = 0; r < rakeCount; r++)
                    {
                        double t = rakeCount == 1 ? 0.0 : (-1.0 + (2.0 * r / (rakeCount - 1)));
                        double ly = t * halfHeight;
                        guides.Add([LocalToWorld(-halfScan, ly), LocalToWorld(halfScan, ly)]);
                    }
                    // Direction arrow on central tooth
                    double arrowSize = Math.Clamp(halfScan * 0.14, 4.0, 10.0);
                    guides.Add([LocalToWorld(halfScan - arrowSize, -arrowSize * 0.6), LocalToWorld(halfScan, 0)]);
                    guides.Add([LocalToWorld(halfScan - arrowSize, arrowSize * 0.6), LocalToWorld(halfScan, 0)]);
                }
                else if (mode == MetrologyMeasurementMode.SpokeCircleFit)
                {
                    double nominalRadius = Math.Max(8.0, Math.Min(target.Width, target.Height) * 0.40);
                    double radialScan = Math.Max(16.0, nominalRadius * 0.78);
                    double innerR = Math.Max(3.0, nominalRadius - (radialScan * 0.5));
                    double outerR = nominalRadius + (radialScan * 0.5);
                    guides.Add(BuildCirclePolygon(center, innerR, 36));
                    guides.Add(BuildCirclePolygon(center, outerR, 44));

                    int spokes = Math.Clamp(parameters.CaliperRakeCount, 8, 24);
                    for (int s = 0; s < spokes; s++)
                    {
                        double a = 2.0 * Math.PI * s / spokes;
                        double ca = Math.Cos(a);
                        double sa = Math.Sin(a);
                        guides.Add([
                            new PointD(center.X + (ca * innerR), center.Y + (sa * innerR)),
                            new PointD(center.X + (ca * outerR), center.Y + (sa * outerR))
                        ]);
                    }
                }
                break;
            }

            case InspectionSuiteAlgorithmKind.Curvilinear:
            {
                double halfSpan = Math.Max(16.0, target.Width * 0.42);
                double halfBead = Math.Clamp(parameters.NominalBeadWidth * 0.5, 2.0, Math.Max(4.0, target.Height * 0.42));
                guides.Add([LocalToWorld(-halfSpan, -halfBead), LocalToWorld(halfSpan, -halfBead)]);
                guides.Add([LocalToWorld(-halfSpan, halfBead), LocalToWorld(halfSpan, halfBead)]);
                guides.Add([LocalToWorld(-halfSpan, 0), LocalToWorld(halfSpan, 0)]);
                break;
            }

            case InspectionSuiteAlgorithmKind.Calibration:
            {
                double stepX = Math.Max(12.0, target.Width * 0.35);
                double stepY = Math.Max(12.0, target.Height * 0.35);
                for (int r = -1; r <= 1; r++)
                {
                    guides.Add([LocalToWorld(-stepX, r * stepY), LocalToWorld(stepX, r * stepY)]);
                }
                for (int c = -1; c <= 1; c++)
                {
                    guides.Add([LocalToWorld(c * stepX, -stepY), LocalToWorld(c * stepX, stepY)]);
                }
                break;
            }

            case InspectionSuiteAlgorithmKind.CornerAlignment:
            {
                double armX = Math.Max(36.0, target.Width * 0.44);
                double armY = Math.Max(36.0, target.Height * 0.44);
                double margin = Math.Clamp(parameters.CornerApexIgnoreMargin, 16.0, Math.Min(armX, armY) * 0.65);
                guides.Add([LocalToWorld(margin, 0), LocalToWorld(armX, 0)]);
                guides.Add([LocalToWorld(0, margin), LocalToWorld(0, armY)]);
                int teeth = Math.Clamp(parameters.CaliperRakeCount, 5, 16);
                for (int t = 0; t < teeth; t++)
                {
                    double u = teeth == 1 ? 0.5 : (double)t / (teeth - 1);
                    double xPos = margin + (u * (armX - margin));
                    double yPos = margin + (u * (armY - margin));
                    guides.Add([LocalToWorld(xPos, -14), LocalToWorld(xPos, 14)]);
                    guides.Add([LocalToWorld(-14, yPos), LocalToWorld(14, yPos)]);
                }
                break;
            }

            case InspectionSuiteAlgorithmKind.IndustrialOcrOcv:
            case InspectionSuiteAlgorithmKind.LightweightOcr:
            case InspectionSuiteAlgorithmKind.CpuDeepOcr:
            case InspectionSuiteAlgorithmKind.PpOcrOnnx:
            {
                const double MinPpOcrGuideHalfWidthPx = 12.0;
                const double MinPpOcrGuideHalfHeightPx = 6.0;
                const double PpOcrInnerWidthRatio = 0.47;
                const double PpOcrInnerHeightRatio = 0.40;
                double halfW = Math.Max(MinPpOcrGuideHalfWidthPx, target.Width * PpOcrInnerWidthRatio);
                double halfH = Math.Max(MinPpOcrGuideHalfHeightPx, target.Height * PpOcrInnerHeightRatio);
                guides.Add([
                    LocalToWorld(-halfW, -halfH),
                    LocalToWorld(halfW, -halfH),
                    LocalToWorld(halfW, halfH),
                    LocalToWorld(-halfW, halfH)
                ]);
                guides.Add([LocalToWorld(-halfW, 0), LocalToWorld(halfW, 0)]);
                break;
            }

            case InspectionSuiteAlgorithmKind.PreprocessingShadingAndRecon:
            case InspectionSuiteAlgorithmKind.AdvancedBlobWatershed:
            case InspectionSuiteAlgorithmKind.DataMatrix2DReader:
            case InspectionSuiteAlgorithmKind.PreprocessingFftAndGabor:
            case InspectionSuiteAlgorithmKind.Barcode1DReader:
            case InspectionSuiteAlgorithmKind.PhotometricStereo:
            case InspectionSuiteAlgorithmKind.GeometricTolerancing:
            {
                double halfW = Math.Max(20.0, target.Width * 0.46);
                double halfH = Math.Max(10.0, target.Height * 0.36);
                guides.Add([
                    LocalToWorld(-halfW, -halfH),
                    LocalToWorld(halfW, -halfH),
                    LocalToWorld(halfW, halfH),
                    LocalToWorld(-halfW, halfH)
                ]);
                guides.Add([LocalToWorld(-halfW, 0), LocalToWorld(halfW, 0)]);
                break;
            }

            case InspectionSuiteAlgorithmKind.AdvancedMetrologyEllipse:
            {
                bool isCaliperHint = target.Name.Contains("캘리퍼", StringComparison.OrdinalIgnoreCase)
                    || target.Name.Contains("Caliper", StringComparison.OrdinalIgnoreCase)
                    || target.Name.Contains("복합", StringComparison.OrdinalIgnoreCase);
                if (isCaliperHint)
                {
                    double halfScan = Math.Max(14.0, target.Width * 0.45);
                    double halfProj = Math.Clamp(parameters.CaliperProjectionWidth * 0.5, 3.0, Math.Max(4.0, target.Height * 0.42));
                    guides.Add([
                        LocalToWorld(-halfScan, -halfProj),
                        LocalToWorld(halfScan, -halfProj),
                        LocalToWorld(halfScan, halfProj),
                        LocalToWorld(-halfScan, halfProj)
                    ]);
                    guides.Add([LocalToWorld(-halfScan, 0), LocalToWorld(halfScan, 0)]);
                }
                else
                {
                    double nomA = Math.Max(10.0, target.Width * 0.38);
                    double nomB = Math.Max(8.0, target.Height * 0.32);
                    int spokes = Math.Clamp(parameters.CaliperRakeCount, 12, 32);
                    for (int s = 0; s < spokes; s++)
                    {
                        double t = 2.0 * Math.PI * s / spokes;
                        double ct = Math.Cos(t);
                        double st = Math.Sin(t);
                        guides.Add([
                            LocalToWorld(nomA * 0.72 * ct, nomB * 0.72 * st),
                            LocalToWorld(nomA * 1.24 * ct, nomB * 1.24 * st)
                        ]);
                    }
                }
                break;
            }
        }

        return guides.ToImmutable();
    }

    private static ImmutableArray<InspectionSuiteItemResult> RunGoldenVariation(
        MatchingImageReader reader,
        int imageWidth,
        int imageHeight,
        IReadOnlyList<InspectionSuiteRoiInput> targets,
        IReadOnlyList<InspectionSuiteRoiInput> references,
        InspectionSuiteParameters parameters,
        InspectionWorkspace workspace,
        CancellationToken token)
    {
        var results = ImmutableArray.CreateBuilder<InspectionSuiteItemResult>(targets.Count);
        IReadOnlyList<InspectionSuiteRoiInput> trainRois = references.Count > 0 ? references : [targets[0]];
        int roiWidth = Math.Clamp((int)Math.Round(trainRois[0].Width), 16, 512);
        int roiHeight = Math.Clamp((int)Math.Round(trainRois[0].Height), 16, 512);

        var trainSamples = new List<VariationTrainingSample>();
        var cropBuffers = new List<(VisionAlgorithm.GrayImage Crop, FixturePose2D LocalPose)>();

        for (int i = 0; i < trainRois.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var refRoi = trainRois[i];
            var (crop, localCenter, _, _) = ReadLocalCrop(reader, imageWidth, imageHeight, refRoi, marginPixels: 16, token);
            var basePose = new FixturePose2D(localCenter.X, localCenter.Y, (float)refRoi.AngleRadians, 1.0f);
            cropBuffers.Add((crop, basePose));
        }

        // If only 1 reference ROI was given, synthesize 3 slight subpixel jitter views (+/- 0.25 px) so Welford variance is well-conditioned
        for (int i = 0; i < cropBuffers.Count; i++)
        {
            var (crop, pose) = cropBuffers[i];
            trainSamples.Add(new VariationTrainingSample(crop.Pixels.ToArray(), crop.Width, crop.Height, crop.Stride, pose));
            if (cropBuffers.Count == 1)
            {
                trainSamples.Add(new VariationTrainingSample(crop.Pixels.ToArray(), crop.Width, crop.Height, crop.Stride,
                    new FixturePose2D(pose.CenterX + 0.25f, pose.CenterY - 0.20f, pose.AngleRad, 1.0f)));
                trainSamples.Add(new VariationTrainingSample(crop.Pixels.ToArray(), crop.Width, crop.Height, crop.Stride,
                    new FixturePose2D(pose.CenterX - 0.25f, pose.CenterY + 0.20f, pose.AngleRad, 1.0f)));
            }
        }

        var spec = new VariationTrainingSpec(
            absoluteMinimumThreshold: 10.0f,
            sigmaMultiplier: Math.Clamp(parameters.VariationStdMultiplier, 1.0f, 10.0f),
            edgeJitterTolerancePixels: Math.Clamp(parameters.VariationEdgeMultiplier, 0.0f, 3.0f));

        VariationModel model = VariationModelTrainer.Train(trainSamples.ToArray(), roiWidth, roiHeight, spec, workspace);
        byte[] maskBuf = new byte[roiWidth * roiHeight];

        for (int i = 0; i < targets.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var target = targets[i];
            long startTick = Stopwatch.GetTimestamp();

            var (crop, localCenter, offsetX, offsetY) = ReadLocalCrop(reader, imageWidth, imageHeight, target, marginPixels: 16, token);
            float scale = (float)Math.Sqrt((target.Width * target.Height) / ((double)roiWidth * roiHeight));
            var localPose = new FixturePose2D(localCenter.X, localCenter.Y, (float)target.AngleRadians, Math.Clamp(scale, 0.25f, 4.0f));

            VariationInspectionResult res = VariationModelInspector.Inspect(
                crop.Pixels.Span,
                crop.Width,
                crop.Height,
                crop.Stride,
                localPose,
                model,
                minimumDefectAreaPixels: Math.Clamp(parameters.GoldenMinDefectAreaPixels, 1, 500),
                workspace: workspace,
                binaryDefectMaskOut: maskBuf);

            var defectPts = ImmutableArray.CreateBuilder<PointD>();
            int step = Math.Max(1, (int)Math.Sqrt(Math.Max(1, res.DefectPixelCount / 120)));
            for (int y = 0; y < roiHeight; y += step)
            {
                for (int x = 0; x < roiWidth; x += step)
                {
                    if (maskBuf[(y * roiWidth) + x] != 0)
                    {
                        float lx = (x + 0.5f) - (roiWidth * 0.5f);
                        float ly = (y + 0.5f) - (roiHeight * 0.5f);
                        Vector2 wp = localPose.LocalToWorld(lx, ly);
                        defectPts.Add(new PointD(wp.X + offsetX, wp.Y + offsetY));
                    }
                }
            }

            double elapsedUs = (Stopwatch.GetTimestamp() - startTick) * 1_000_000.0 / Stopwatch.Frequency;
            bool isPass = res.IsDefectFree;
            float defectRatio = (float)res.DefectPixelCount / Math.Max(1, roiWidth * roiHeight);
            string statusTag = isPass ? "정상(GOOD)" : "변이불량(NG)";
            string badge = $"{statusTag} · 초과={res.DefectPixelCount}px";
            string detail = $"{statusTag} · 초과 픽셀={res.DefectPixelCount}개 ({defectRatio * 100f:F2}%) · 최대 초과 편차={res.MaxExcessDifference:F1} GV (Z={res.MaxNormalizedZScore:F2})";

            results.Add(new InspectionSuiteItemResult(
                target.RoiId,
                target.Name,
                isPass,
                badge,
                detail,
                target.Center,
                target.AngleRadians,
                target.Outline,
                [],
                [],
                [],
                defectPts.ToImmutable(),
                elapsedUs));
        }

        return results.MoveToImmutable();
    }

    private static ImmutableArray<InspectionSuiteItemResult> RunBlobRle(
        MatchingImageReader reader,
        int imageWidth,
        int imageHeight,
        IReadOnlyList<InspectionSuiteRoiInput> targets,
        InspectionSuiteParameters parameters,
        InspectionWorkspace workspace,
        CancellationToken token)
    {
        var results = ImmutableArray.CreateBuilder<InspectionSuiteItemResult>(targets.Count);
        byte minThresh = (byte)Math.Clamp(parameters.ThresholdIntensity, 1, 255);
        int morphRadius = Math.Clamp(parameters.MorphologyRadius, 0, 12);

        for (int i = 0; i < targets.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var target = targets[i];
            long startTick = Stopwatch.GetTimestamp();

            var (crop, _, offsetX, offsetY) = ReadLocalCrop(reader, imageWidth, imageHeight, target, marginPixels: 0, token);
            RleRegion region = RleRegion.Threshold(crop.Pixels.Span, crop.Width, crop.Height, crop.Stride, minIntensity: minThresh, maxIntensity: 255);
            if (morphRadius > 0)
            {
                region = RleMorphology.OpenBox(region, morphRadius, morphRadius);
            }

            List<BlobComponentFeature> blobs = RleBlobAnalyzer.AnalyzeComponents(region, minimumAreaPixels: Math.Clamp(parameters.BlobMinimumAreaPixels, 1, 5000), workspace);
            var primaryPts = ImmutableArray.CreateBuilder<PointD>(blobs.Count);
            var contours = ImmutableArray.CreateBuilder<ImmutableArray<PointD>>(blobs.Count * 2);

            for (int b = 0; b < blobs.Count; b++)
            {
                BlobComponentFeature blob = blobs[b];
                PointD worldCentroid = new(blob.Centroid.X + offsetX, blob.Centroid.Y + offsetY);
                primaryPts.Add(worldCentroid);

                double left = blob.MinCol + offsetX;
                double top = blob.MinRow + offsetY;
                double right = left + blob.BoundingWidth;
                double bottom = top + blob.BoundingHeight;
                contours.Add([
                    new PointD(left, top),
                    new PointD(right, top),
                    new PointD(right, bottom),
                    new PointD(left, bottom)
                ]);

                double axisLen = Math.Max(6.0, Math.Min(blob.BoundingWidth, blob.BoundingHeight) * 0.45);
                double dx = Math.Cos(blob.PrincipalAxisAngleRad) * axisLen;
                double dy = Math.Sin(blob.PrincipalAxisAngleRad) * axisLen;
                contours.Add([
                    new PointD(worldCentroid.X - dx, worldCentroid.Y - dy),
                    new PointD(worldCentroid.X + dx, worldCentroid.Y + dy)
                ]);
            }

            double elapsedUs = (Stopwatch.GetTimestamp() - startTick) * 1_000_000.0 / Stopwatch.Frequency;
            bool isPass = blobs.Count > 0;
            long totalArea = 0;
            for (int b = 0; b < blobs.Count; b++)
            {
                totalArea += blobs[b].AreaPixels;
            }

            string topBlobInfo = blobs.Count > 0
                ? $"최대 면적={blobs[0].AreaPixels}px, 중심=({blobs[0].Centroid.X + offsetX:F2}, {blobs[0].Centroid.Y + offsetY:F2}), 주축={blobs[0].PrincipalAxisAngleRad * (180.0 / Math.PI):F1}°, 이심률={blobs[0].Eccentricity:F3}"
                : "조건 만족 블롭 없음";

            results.Add(new InspectionSuiteItemResult(
                target.RoiId,
                target.Name,
                isPass,
                $"RLE Blobs={blobs.Count} · Area={totalArea}px",
                $"검출 블롭={blobs.Count}개 (총 면적={totalArea}px, 런 수={region.Runs.Count}) · {topBlobInfo}",
                target.Center,
                target.AngleRadians,
                target.Outline,
                contours.ToImmutable(),
                primaryPts.ToImmutable(),
                [],
                [],
                elapsedUs));
        }

        return results.MoveToImmutable();
    }

    private static ImmutableArray<InspectionSuiteItemResult> RunCurvilinear(
        MatchingImageReader reader,
        int imageWidth,
        int imageHeight,
        IReadOnlyList<InspectionSuiteRoiInput> targets,
        InspectionSuiteParameters parameters,
        InspectionWorkspace workspace,
        CancellationToken token)
    {
        var results = ImmutableArray.CreateBuilder<InspectionSuiteItemResult>(targets.Count);
        float sigma = Math.Clamp(parameters.GaussianSigma, 0.7f, 6.0f);
        float minContrast = Math.Clamp(parameters.MinimumContrast, 3.0f, 150.0f);
        float nominalWidth = Math.Clamp(parameters.NominalBeadWidth, 3.0f, 120.0f);
        const int wpCount = 15;
        Span<Vector2> waypoints = stackalloc Vector2[wpCount];

        for (int i = 0; i < targets.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var target = targets[i];
            long startTick = Stopwatch.GetTimestamp();

            var (crop, localCenter, offsetX, offsetY) = ReadLocalCrop(reader, imageWidth, imageHeight, target, marginPixels: 12, token);
            Span<float> floatImg = workspace.GetFloatBufferA(crop.Width * crop.Height);
            for (int r = 0; r < crop.Height; r++)
            {
                ReadOnlySpan<byte> srcRow = crop.Pixels.Span.Slice(r * crop.Stride, crop.Width);
                Span<float> dstRow = floatImg.Slice(r * crop.Width, crop.Width);
                for (int c = 0; c < crop.Width; c++)
                {
                    dstRow[c] = srcRow[c];
                }
            }

            List<RidgeCenterlinePoint> ridges = HessianRidgeDetector.ExtractCenterlinePoints(
                floatImg,
                crop.Width,
                crop.Height,
                gaussianSigma: sigma,
                minSecondDerivativeStrength: minContrast * 0.4f,
                polarity: RidgePolarity.BrightLine,
                workspace: workspace);

            // Inspect bead continuity along the horizontal centerline of the ROI
            var localPose = new FixturePose2D(localCenter.X, localCenter.Y, (float)target.AngleRadians, 1.0f);
            float halfSpan = MathF.Max(16.0f, (float)target.Width * 0.4f);
            for (int w = 0; w < wpCount; w++)
            {
                float t = -1f + (2f * w / (wpCount - 1));
                waypoints[w] = localPose.LocalToWorld(t * halfSpan, 0f);
            }

            BeadContinuityResult bead = BeadContinuityInspector.InspectPath(
                crop.Pixels.Span,
                crop.Width,
                crop.Height,
                crop.Stride,
                waypoints,
                RidgePolarity.BrightLine,
                nominalWidthPixels: nominalWidth,
                minAllowedWidthPixels: nominalWidth * 0.65f,
                maxAllowedWidthPixels: nominalWidth * 1.45f,
                minimumContrast: minContrast,
                workspace: workspace);

            var primaryPts = ImmutableArray.CreateBuilder<PointD>();
            int ridgeStride = Math.Max(1, ridges.Count / 200);
            for (int k = 0; k < ridges.Count; k += ridgeStride)
            {
                Vector2 pt = ridges[k].SubpixelPosition;
                primaryPts.Add(new PointD(pt.X + offsetX, pt.Y + offsetY));
            }

            var defectPts = ImmutableArray.CreateBuilder<PointD>();
            for (int w = 0; w < bead.Samples.Count; w++)
            {
                if (!bead.Samples[w].IsPresent)
                {
                    Vector2 nom = bead.Samples[w].NominalCenterWorld;
                    defectPts.Add(new PointD(nom.X + offsetX, nom.Y + offsetY));
                }
            }

            double elapsedUs = (Stopwatch.GetTimestamp() - startTick) * 1_000_000.0 / Stopwatch.Frequency;
            bool isPass = bead.IsContinuousAndWithinSpec || ridges.Count > 10;
            string badge = $"Steger={ridges.Count}점 · 평균폭={bead.MeanWidthPixels:F2}px";
            string detail = $"Steger 서브픽셀 능선 {ridges.Count}점 · 비드 평균폭={bead.MeanWidthPixels:F2}px (최소={bead.MinWidthPixels:F2}px, 최대={bead.MaxWidthPixels:F2}px, 단선={bead.GapStationCount}, 협소={bead.NeckingStationCount}, 과다={bead.BulgeStationCount})";

            results.Add(new InspectionSuiteItemResult(
                target.RoiId,
                target.Name,
                isPass,
                badge,
                detail,
                target.Center,
                target.AngleRadians,
                target.Outline,
                [],
                primaryPts.ToImmutable(),
                [],
                defectPts.ToImmutable(),
                elapsedUs,
                BuildGeometricGuidesForRoi(target, InspectionSuiteAlgorithmKind.Curvilinear, parameters)));
        }

        return results.MoveToImmutable();
    }

    private static ImmutableArray<InspectionSuiteItemResult> RunFewShot(
        MatchingImageReader reader,
        int imageWidth,
        int imageHeight,
        IReadOnlyList<InspectionSuiteRoiInput> targets,
        IReadOnlyList<InspectionSuiteRoiInput> references,
        InspectionSuiteParameters parameters,
        InspectionWorkspace workspace,
        CancellationToken token)
    {
        var results = ImmutableArray.CreateBuilder<InspectionSuiteItemResult>(targets.Count);
        const int patchW = 48;
        const int patchH = 48;
        var trainingExamples = new List<FewShotTrainingExample>();

        ReadOnlySpan<(float Dx, float Dy)> subpixelOffsets =
        [
            (0f, 0f),
            (-0.35f, -0.25f),
            (0.35f, 0.25f),
            (-0.25f, 0.35f),
            (0.25f, -0.35f)
        ];

        IReadOnlyList<InspectionSuiteRoiInput> sourceRefs = references.Count > 0 ? references : [targets[0]];
        for (int i = 0; i < sourceRefs.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var refRoi = sourceRefs[i];
            for (int j = 0; j < subpixelOffsets.Length; j++)
            {
                var (dx, dy) = subpixelOffsets[j];
                float[] emb = ExtractCompactGeMEmbedding(reader, imageWidth, imageHeight, refRoi, patchW, patchH, workspace, token, dx, dy);
                trainingExamples.Add(new FewShotTrainingExample(refRoi.ClassLabel, emb));
            }
        }

        int maxClusters = Math.Clamp(parameters.FewShotMaxSubClusters, 1, 5);
        float oodScale = Math.Clamp(parameters.FewShotOodSigmaMultiplier, 1.0f, 10.0f) / 3.5f;
        EmbeddingFewShotClassifier model = EmbeddingFewShotClassifier.Train(trainingExamples.ToArray(), maxSubClustersPerClass: maxClusters);

        for (int i = 0; i < targets.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var target = targets[i];
            long startTick = Stopwatch.GetTimestamp();

            float[] queryEmb = ExtractCompactGeMEmbedding(reader, imageWidth, imageHeight, target, patchW, patchH, workspace, token);
            FewShotPrediction pred = model.Predict(queryEmb, workspace);
            float effectiveThreshold = pred.RejectionThreshold * oodScale;
            bool isOod = pred.MahalanobisDistance > effectiveThreshold;

            double elapsedUs = (Stopwatch.GetTimestamp() - startTick) * 1_000_000.0 / Stopwatch.Frequency;
            bool isPass = !isOod && pred.PredictedClassLabel == 0;
            string classTag = isOod
                ? "OOD(미등록 이상치)"
                : (pred.PredictedClassLabel == 0 ? "Good(정상)" : $"Class {pred.PredictedClassLabel}(불량)");

            results.Add(new InspectionSuiteItemResult(
                target.RoiId,
                target.Name,
                isPass,
                $"{classTag} · D_M={pred.MahalanobisDistance:F2}",
                $"{classTag} · 마할라노비스 거리={pred.MahalanobisDistance:F3} (임계치={effectiveThreshold:F2}) · 신뢰도={pred.ConfidenceScore * 100f:F1}%",
                target.Center,
                target.AngleRadians,
                target.Outline,
                [],
                isPass ? [target.Center] : [],
                [],
                !isPass ? [target.Center] : [],
                elapsedUs));
        }

        return results.MoveToImmutable();
    }

    private static ImmutableArray<InspectionSuiteItemResult> RunPatchAnomaly(
        MatchingImageReader reader,
        int imageWidth,
        int imageHeight,
        IReadOnlyList<InspectionSuiteRoiInput> targets,
        IReadOnlyList<InspectionSuiteRoiInput> references,
        InspectionSuiteParameters parameters,
        InspectionWorkspace workspace,
        CancellationToken token)
    {
        var results = ImmutableArray.CreateBuilder<InspectionSuiteItemResult>(targets.Count);
        IReadOnlyList<InspectionSuiteRoiInput> trainRois = references.Count > 0 ? references : [targets[0]];
        int patchSize = Math.Clamp(parameters.PatchSize, 4, 32);
        int roiWidth = Math.Max(patchSize * 2, ((int)Math.Clamp(trainRois[0].Width, 32, 256) / patchSize) * patchSize);
        int roiHeight = Math.Max(patchSize * 2, ((int)Math.Clamp(trainRois[0].Height, 32, 256) / patchSize) * patchSize);

        var normalPatches = new List<float[]>();
        for (int i = 0; i < trainRois.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var refRoi = trainRois[i];
            var (crop, localCenter, _, _) = ReadLocalCrop(reader, imageWidth, imageHeight, refRoi, marginPixels: 16, token);
            float scale = (float)Math.Sqrt((refRoi.Width * refRoi.Height) / ((double)roiWidth * roiHeight));
            var basePose = new FixturePose2D(localCenter.X, localCenter.Y, (float)refRoi.AngleRadians, Math.Clamp(scale, 0.25f, 4.0f));

            float[] warped = new float[roiWidth * roiHeight];
            SimdAffineWarper.WarpRectifiedRoi(crop.Pixels.Span, crop.Width, crop.Height, crop.Stride, basePose, roiWidth, roiHeight, warped);
            normalPatches.Add(warped);

            if (trainRois.Count < 4)
            {
                ReadOnlySpan<(float Dx, float Dy)> patchOffsets =
                [
                    (-0.35f, -0.25f),
                    (0.35f, 0.25f),
                    (-0.25f, 0.35f)
                ];
                for (int j = 0; j < patchOffsets.Length; j++)
                {
                    var (dx, dy) = patchOffsets[j];
                    var jitterPose = new FixturePose2D(basePose.CenterX + dx, basePose.CenterY + dy, basePose.AngleRad, basePose.Scale);
                    float[] jitterWarped = new float[roiWidth * roiHeight];
                    SimdAffineWarper.WarpRectifiedRoi(crop.Pixels.Span, crop.Width, crop.Height, crop.Stride, jitterPose, roiWidth, roiHeight, jitterWarped);
                    normalPatches.Add(jitterWarped);
                }
            }
        }

        PadimHeatmapDetector detector = PadimHeatmapDetector.Train(normalPatches, roiWidth, roiHeight, patchSize: patchSize);
        float[] heatmap = new float[roiWidth * roiHeight];

        for (int i = 0; i < targets.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var target = targets[i];
            long startTick = Stopwatch.GetTimestamp();

            var (crop, localCenter, offsetX, offsetY) = ReadLocalCrop(reader, imageWidth, imageHeight, target, marginPixels: 16, token);
            float scale = (float)Math.Sqrt((target.Width * target.Height) / ((double)roiWidth * roiHeight));
            var localPose = new FixturePose2D(localCenter.X, localCenter.Y, (float)target.AngleRadians, Math.Clamp(scale, 0.25f, 4.0f));

            Span<float> targetWarped = workspace.GetFloatBufferA(roiWidth * roiHeight);
            SimdAffineWarper.WarpRectifiedRoi(crop.Pixels.Span, crop.Width, crop.Height, crop.Stride, localPose, roiWidth, roiHeight, targetWarped);

            PadimAnomalyResult res = detector.Inspect(targetWarped, workspace, heatmap);
            float padimThresholdScale = Math.Clamp(1.0f + ((parameters.PadimRegularizationLambda - 0.01f) * 2.0f), 0.5f, 3.0f);
            float effectivePadimThreshold = detector.AnomalyThreshold * padimThresholdScale;
            float peakLocalX = res.PeakAnomalyLocalPosition.X - (roiWidth * 0.5f);
            float peakLocalY = res.PeakAnomalyLocalPosition.Y - (roiHeight * 0.5f);
            Vector2 peakWorldInCrop = localPose.LocalToWorld(peakLocalX, peakLocalY);
            PointD peakWorld = new(peakWorldInCrop.X + offsetX, peakWorldInCrop.Y + offsetY);

            double elapsedUs = (Stopwatch.GetTimestamp() - startTick) * 1_000_000.0 / Stopwatch.Frequency;
            bool isPass = res.ImageAnomalyScore <= effectivePadimThreshold;
            string statusTag = isPass ? "PaDiM 정상(OK)" : "PaDiM 이상(ANOMALY)";

            results.Add(new InspectionSuiteItemResult(
                target.RoiId,
                target.Name,
                isPass,
                $"{statusTag} · Max={res.ImageAnomalyScore:F2}",
                $"{statusTag} · 최대 패치 마할라노비스 점수={res.ImageAnomalyScore:F3} (임계치={effectivePadimThreshold:F3}, 초과픽셀={res.AnomalousPixelCount}) · 피크 좌표=({peakWorld.X:F1}, {peakWorld.Y:F1})",
                target.Center,
                target.AngleRadians,
                target.Outline,
                [],
                isPass ? [peakWorld] : [],
                [],
                !isPass ? [peakWorld] : [],
                elapsedUs));
        }

        return results.MoveToImmutable();
    }

    private static ImmutableArray<InspectionSuiteItemResult> RunCalibration(
        IReadOnlyList<InspectionSuiteRoiInput> targets,
        InspectionSuiteParameters parameters,
        int imageWidth,
        int imageHeight)
    {
        var results = ImmutableArray.CreateBuilder<InspectionSuiteItemResult>(targets.Count);
        float pitchMm = Math.Clamp(parameters.GridPitchMm, 0.5f, 500.0f);
        BrownConradyModel lensModel = new(
            focalLengthX: Math.Max(imageWidth, imageHeight),
            focalLengthY: Math.Max(imageWidth, imageHeight),
            principalPointX: imageWidth * 0.5f,
            principalPointY: imageHeight * 0.5f,
            k1: Math.Clamp(parameters.LensK1, -0.5f, 0.5f),
            k2: 0.01f,
            p1: 0.0005f,
            p2: -0.0005f);

        Span<Vector2> pixelPts = stackalloc Vector2[9];
        Span<Vector2> worldMmPts = stackalloc Vector2[9];

        for (int i = 0; i < targets.Count; i++)
        {
            var target = targets[i];
            long startTick = Stopwatch.GetTimestamp();

            var localPose = new FixturePose2D((float)target.Center.X, (float)target.Center.Y, (float)target.AngleRadians, 1.0f);
            float stepX = MathF.Max(12.0f, (float)target.Width * 0.35f);
            float stepY = MathF.Max(12.0f, (float)target.Height * 0.35f);

            var gridPoints = ImmutableArray.CreateBuilder<PointD>(9);
            var undistortedPoints = ImmutableArray.CreateBuilder<PointD>(9);

            int idx = 0;
            for (int r = -1; r <= 1; r++)
            {
                for (int c = -1; c <= 1; c++)
                {
                    Vector2 p = localPose.LocalToWorld(c * stepX, r * stepY);
                    pixelPts[idx] = p;
                    worldMmPts[idx] = new Vector2(c * pitchMm, r * pitchMm);
                    gridPoints.Add(new PointD(p.X, p.Y));

                    Vector2 distorted = BrownConradyUndistorter.DistortPixel(p, lensModel);
                    Vector2 recovered = BrownConradyUndistorter.UndistortPixel(distorted, lensModel);
                    undistortedPoints.Add(new PointD(recovered.X, recovered.Y));
                    idx++;
                }
            }

            PlanarHomography2D homography = NPointHomographyCalibrator.Calibrate(pixelPts, worldMmPts);
            Vector2 centerMm = homography.PixelToWorldMm(new Vector2((float)target.Center.X, (float)target.Center.Y));
            Vector2 cornerDistorted = BrownConradyUndistorter.DistortPixel(pixelPts[8], lensModel);
            Vector2 cornerRecovered = BrownConradyUndistorter.UndistortPixel(cornerDistorted, lensModel);
            float lensRoundTripErr = Vector2.Distance(pixelPts[8], cornerRecovered);

            double elapsedUs = (Stopwatch.GetTimestamp() - startTick) * 1_000_000.0 / Stopwatch.Frequency;
            bool isPass = homography.RmsReprojectionErrorMm < 0.05 && lensRoundTripErr < 0.01f;

            results.Add(new InspectionSuiteItemResult(
                target.RoiId,
                target.Name,
                isPass,
                $"Homography RMS={homography.RmsReprojectionErrorMm:E2}mm",
                $"9-Point 호모그래피 재투영 RMS={homography.RmsReprojectionErrorMm:E3}mm · 중심 물리좌표=({centerMm.X:F3}, {centerMm.Y:F3})mm · 렌즈 왜곡 역보정 오차={lensRoundTripErr:E3}px",
                target.Center,
                target.AngleRadians,
                target.Outline,
                [],
                gridPoints.MoveToImmutable(),
                undistortedPoints.MoveToImmutable(),
                [],
                elapsedUs,
                BuildGeometricGuidesForRoi(target, InspectionSuiteAlgorithmKind.Calibration, parameters)));
        }

        return results.MoveToImmutable();
    }

    private static float[] ExtractCompactGeMEmbedding(
        MatchingImageReader reader,
        int imageWidth,
        int imageHeight,
        InspectionSuiteRoiInput roi,
        int patchW,
        int patchH,
        InspectionWorkspace workspace,
        CancellationToken token,
        float subpixelOffsetX = 0f,
        float subpixelOffsetY = 0f)
    {
        var (crop, localCenter, _, _) = ReadLocalCrop(reader, imageWidth, imageHeight, roi, marginPixels: 12, token);
        float scale = (float)Math.Sqrt((roi.Width * roi.Height) / ((double)patchW * patchH));
        var pose = new FixturePose2D(
            localCenter.X + subpixelOffsetX,
            localCenter.Y + subpixelOffsetY,
            (float)roi.AngleRadians,
            Math.Clamp(scale, 0.25f, 4.0f));
        Span<float> patch = workspace.GetFloatBufferA(patchW * patchH);
        SimdAffineWarper.WarpRectifiedRoi(crop.Pixels.Span, crop.Width, crop.Height, crop.Stride, pose, patchW, patchH, patch);

        // Extract 24-D multi-zone gradient orientation + intensity moments
        const int gridX = 2;
        const int gridY = 2;
        const int binsPerCell = 6;
        float[] embedding = new float[gridX * gridY * binsPerCell];
        int cellW = patchW / gridX;
        int cellH = patchH / gridY;

        for (int cy = 0; cy < gridY; cy++)
        {
            for (int cx = 0; cx < gridX; cx++)
            {
                int baseIdx = ((cy * gridX) + cx) * binsPerCell;
                for (int y = Math.Max(1, cy * cellH); y < Math.Min(patchH - 1, (cy + 1) * cellH); y++)
                {
                    for (int x = Math.Max(1, cx * cellW); x < Math.Min(patchW - 1, (cx + 1) * cellW); x++)
                    {
                        float gx = patch[(y * patchW) + x + 1] - patch[(y * patchW) + x - 1];
                        float gy = patch[((y + 1) * patchW) + x] - patch[((y - 1) * patchW) + x];
                        float mag = MathF.Sqrt((gx * gx) + (gy * gy));
                        float angle = MathF.Atan2(MathF.Abs(gy), MathF.Abs(gx));
                        int bin = Math.Clamp((int)(angle / (MathF.PI * 0.5f) * (binsPerCell - 2)), 0, binsPerCell - 3);
                        embedding[baseIdx + bin] += mag;
                        embedding[baseIdx + (binsPerCell - 2)] += patch[(y * patchW) + x] * (1f / 255f);
                        embedding[baseIdx + (binsPerCell - 1)] += mag * mag * 0.01f;
                    }
                }
            }
        }

        return embedding;
    }

    private static (VisionAlgorithm.GrayImage Crop, Vector2 LocalCenter, int OffsetX, int OffsetY) ReadLocalCrop(
        MatchingImageReader reader,
        int imageWidth,
        int imageHeight,
        InspectionSuiteRoiInput roi,
        int marginPixels,
        CancellationToken token)
    {
        double absCos = Math.Abs(Math.Cos(roi.AngleRadians));
        double absSin = Math.Abs(Math.Sin(roi.AngleRadians));
        int radiusX = (int)Math.Ceiling((roi.Width * absCos + roi.Height * absSin) * 0.5) + marginPixels;
        int radiusY = (int)Math.Ceiling((roi.Width * absSin + roi.Height * absCos) * 0.5) + marginPixels;

        int left = Math.Clamp((int)Math.Floor(roi.Center.X) - radiusX, 0, Math.Max(0, imageWidth - 1));
        int top = Math.Clamp((int)Math.Floor(roi.Center.Y) - radiusY, 0, Math.Max(0, imageHeight - 1));
        int right = Math.Clamp((int)Math.Ceiling(roi.Center.X) + radiusX, left + 1, imageWidth);
        int bottom = Math.Clamp((int)Math.Ceiling(roi.Center.Y) + radiusY, top + 1, imageHeight);

        var crop = reader.Read(
            left,
            top,
            right - left,
            bottom - top,
            token,
            maximumPixels: VisionAlgorithm.OpenSrc.PatternMatching.CorrelationTemplate.MaximumSearchPixels);
        var localCenter = new Vector2((float)(roi.Center.X - left), (float)(roi.Center.Y - top));
        return (crop, localCenter, left, top);
    }

    private static ImmutableArray<PointD> BuildCirclePolygon(PointD center, double radius, int segments)
    {
        var pts = ImmutableArray.CreateBuilder<PointD>(segments);
        for (int i = 0; i < segments; i++)
        {
            double angle = 2.0 * Math.PI * i / segments;
            pts.Add(new PointD(center.X + (Math.Cos(angle) * radius), center.Y + (Math.Sin(angle) * radius)));
        }
        return pts.MoveToImmutable();
    }

    private static InspectionSuiteItemResult CreateZoneMarker(
        string name,
        PointD center,
        double width,
        double height,
        bool isPass,
        string badge,
        string detail)
    {
        double hw = width * 0.5;
        double hh = height * 0.5;
        ImmutableArray<PointD> bounds =
        [
            new PointD(center.X - hw, center.Y - hh),
            new PointD(center.X + hw, center.Y - hh),
            new PointD(center.X + hw, center.Y + hh),
            new PointD(center.X - hw, center.Y + hh)
        ];

        return new InspectionSuiteItemResult(
            Guid.NewGuid(),
            name,
            isPass,
            badge,
            detail,
            center,
            0.0,
            bounds,
            [],
            [center],
            [],
            [],
            0.0);
    }

    public static string GetAlgorithmShortName(InspectionSuiteAlgorithmKind kind) => kind switch
    {
        InspectionSuiteAlgorithmKind.Metrology => "1. 서브픽셀 캘리퍼 · RANSAC 직선 · Taubin 원 계측",
        InspectionSuiteAlgorithmKind.GoldenVariation => "2. 통계적 골든 마스터 변이 검사 (Welford + Sobel)",
        InspectionSuiteAlgorithmKind.BlobRle => "3. RLE 블롭 해석 · 구간 모폴로지",
        InspectionSuiteAlgorithmKind.Curvilinear => "4. Steger 헤시안 곡선 · 비드 연속성 검사",
        InspectionSuiteAlgorithmKind.FewShot => "5. 소량 샘플 분류 · OOD 이상 탐지 (Few-Shot)",
        InspectionSuiteAlgorithmKind.PatchAnomaly => "6. PaDiM 패치 이상치 히트맵 탐지",
        InspectionSuiteAlgorithmKind.Calibration => "7. 9-Point 호모그래피 · Brown-Conrady 왜곡 보정",
        InspectionSuiteAlgorithmKind.CornerAlignment => "8. 2-Rake 서브픽셀 가상 코너 · 대각 정합",
        InspectionSuiteAlgorithmKind.IndustrialOcrOcv
            or InspectionSuiteAlgorithmKind.LightweightOcr
            or InspectionSuiteAlgorithmKind.CpuDeepOcr
            or InspectionSuiteAlgorithmKind.PpOcrOnnx => "9. OCR / OCV (PP-OCRv4 범용 판독 · 티칭 OCV 품질 검증)",
        InspectionSuiteAlgorithmKind.AutoTeaching => "11. 멀티파츠 풀 자동 인식 · 하위 ROI 자동 티칭",
        InspectionSuiteAlgorithmKind.PreprocessingShadingAndRecon => "13. [전처리] 2D 다항식 셰이딩 보정 · 비등방성 확산 · 형태학적 재구성",
        InspectionSuiteAlgorithmKind.AdvancedMetrologyEllipse => "14. [본검사] 복합 스코어링 캘리퍼(First/Last/Span) · Fitzgibbon 타원 피팅",
        InspectionSuiteAlgorithmKind.AdvancedBlobWatershed => "15. [본검사] RLE 고급 형상(Convex Hull·MinAreaRect·Hole) · 거리변환 접촉 블롭 분리",
        InspectionSuiteAlgorithmKind.DataMatrix2DReader => "16. [본검사] 산업용 2D DataMatrix ECC 200 리더 · ISO/IEC 15415 품질 등급",
        InspectionSuiteAlgorithmKind.PreprocessingFftAndGabor => "17. [전처리] 2D FFT 주기적 모아레 노치 · 호모모픽 조명 분리 · Gabor 텍스처",
        InspectionSuiteAlgorithmKind.Barcode1DReader => "18. [본검사] 산업용 1D 바코드(Code 128) 멀티스캔 디코더 · ISO/IEC 15416 품질 등급",
        InspectionSuiteAlgorithmKind.PhotometricStereo => "19. [본검사] 4방향 포토메트릭 스테레오 법선·알베도·3D 평균/가우시안 곡률 결함 검사",
        InspectionSuiteAlgorithmKind.GeometricTolerancing => "20. [본검사] 다중 형상 기하공차(GD&T) 직교도·동심도·데이텀 수직거리 계측",
        _ => kind.ToString()
    };

    private static ImmutableArray<InspectionSuiteItemResult> RunCornerAlignment(
        MatchingImageReader reader,
        int imageWidth,
        int imageHeight,
        IReadOnlyList<InspectionSuiteRoiInput> rois,
        InspectionSuiteParameters parameters,
        CancellationToken token)
    {
        var results = ImmutableArray.CreateBuilder<InspectionSuiteItemResult>(rois.Count);
        var options = new CornerDetectionOptions(
            GaussianSigma: Math.Clamp(parameters.GaussianSigma, 0.5f, 8.0f),
            MinimumContrast: Math.Clamp(parameters.MinimumContrast, 2.0f, 200.0f),
            RansacInlierThresholdPixels: Math.Clamp(parameters.CornerRansacThresholdPixels, 0.1f, 5.0f),
            TukeyIterations: 5,
            OrthogonalityToleranceDeg: Math.Clamp(parameters.CornerOrthogonalityToleranceDeg, 0.5f, 25.0f),
            MinimumInlierRatio: 0.45f);

        ReadOnlySpan<(float Sx, float Sy)> quadrants =
        [
            (1f, 1f),
            (-1f, 1f),
            (-1f, -1f),
            (1f, -1f)
        ];

        foreach (var roi in rois)
        {
            token.ThrowIfCancellationRequested();
            var sw = Stopwatch.StartNew();
            var (crop, localCenter, offsetX, offsetY) = ReadLocalCrop(reader, imageWidth, imageHeight, roi, marginPixels: 36, token);
            var view = new ReadOnlyImageView2D<byte>(crop.Pixels.Span, crop.Width, crop.Height, crop.Stride);

            float halfW = MathF.Max(36f, (float)(roi.Width * 0.46));
            float halfH = MathF.Max(36f, (float)(roi.Height * 0.46));
            float cos = MathF.Cos((float)roi.AngleRadians);
            float sin = MathF.Sin((float)roi.AngleRadians);
            Vector2 axisX = new(cos, sin);
            Vector2 axisY = new(-sin, cos);
            int caliperCount = Math.Max(10, parameters.CaliperRakeCount);
            float projWidth = Math.Clamp(parameters.CaliperProjectionWidth, 3f, 25f);

            VirtualCornerResult bestCorner = VirtualCornerResult.Invalid;
            RakeSearchRegion bestLeg1 = default;
            RakeSearchRegion bestLeg2 = default;
            SubpixelEdgePoint[] bestInliers = [];
            List<SubpixelEdgePoint> bestOutliers = [];

            for (int q = 0; q < quadrants.Length; q++)
            {
                var (sx, sy) = quadrants[q];
                Vector2 legDir1 = axisX * sx;
                Vector2 legDir2 = axisY * sy;

                // 1단계: 모따기 영역 바깥의 외측 직선 구간에서 두 변의 대략적인 위치(approxApex)를 먼저 탐색한다.
                Vector2 approxApex = EstimateCornerApexInRoi(
                    view,
                    localCenter,
                    legDir1,
                    legDir2,
                    halfW,
                    halfH,
                    projWidth,
                    options);

                float relU = Vector2.Dot(approxApex - localCenter, legDir1);
                float relV = Vector2.Dot(approxApex - localCenter, legDir2);
                float availLen1 = Math.Clamp(halfW - relU, 48f, (float)roi.Width * 0.90f);
                float availLen2 = Math.Clamp(halfH - relV, 48f, (float)roi.Height * 0.90f);

                // 모따기(Chamfer) 및 코너 라운딩 구간을 완벽히 건너뛰도록 최소 설정 마진 또는 변 길이의 36% 이상을 ApexIgnoreMargin으로 강제한다.
                float userApexMargin = Math.Clamp(parameters.CornerApexIgnoreMargin, 16.0f, 80.0f);
                float apexMargin1 = MathF.Max(userApexMargin, availLen1 * 0.36f);
                float apexMargin2 = MathF.Max(userApexMargin, availLen2 * 0.36f);

                Vector2 end1 = approxApex + (legDir1 * availLen1);
                Vector2 end2 = approxApex + (legDir2 * availLen2);

                var leg1 = new RakeSearchRegion(
                    StartPoint: approxApex,
                    EndPoint: end1,
                    SearchLength: 28f,
                    ProjectionWidth: projWidth,
                    CaliperCount: caliperCount,
                    ApexIgnoreMargin: apexMargin1,
                    Polarity: CaliperPolarity.Any,
                    ExplicitScanDirection: legDir2);

                var leg2 = new RakeSearchRegion(
                    StartPoint: approxApex,
                    EndPoint: end2,
                    SearchLength: 28f,
                    ProjectionWidth: projWidth,
                    CaliperCount: caliperCount,
                    ApexIgnoreMargin: apexMargin2,
                    Polarity: CaliperPolarity.Any,
                    ExplicitScanDirection: legDir1);

                VirtualCornerResult cand = VirtualCornerAligner.DetectVirtualCornerWithPoints(
                    view,
                    leg1,
                    leg2,
                    options,
                    out SubpixelEdgePoint[] candInliers,
                    out SubpixelEdgePoint[] candOutliers);

                if (cand.IsValid && (!bestCorner.IsValid || cand.InlierRatio > bestCorner.InlierRatio))
                {
                    bestCorner = cand;
                    bestLeg1 = leg1;
                    bestLeg2 = leg2;
                    bestInliers = candInliers;
                    bestOutliers = new List<SubpixelEdgePoint>(candOutliers);

                    // 모따기 구간(Chamfer Zone) 위를 스캔했을 때 2D Gradient Orientation Gate에 의해 기각되는 점들을 수집하여 시각화한다.
                    CollectChamferRejectedPoints(view, approxApex, legDir1, legDir2, apexMargin1, apexMargin2, projWidth, options, bestOutliers);
                }
            }

            sw.Stop();
            double us = sw.Elapsed.TotalMicroseconds;

            PointD worldVertex = bestCorner.IsValid
                ? new PointD(offsetX + bestCorner.CornerX, offsetY + bestCorner.CornerY)
                : roi.Center;
            bool isPass = bestCorner.IsValid;
            float angleDevDeg = bestCorner.IsValid ? MathF.Abs(90.0f - bestCorner.IncludedAngleDeg) : 0f;
            float line1AngleDeg = bestCorner.IsValid ? bestCorner.Line1.AngleRad * (180.0f / MathF.PI) : 0f;
            float line2AngleDeg = bestCorner.IsValid ? bestCorner.Line2.AngleRad * (180.0f / MathF.PI) : 0f;

            string badge = bestCorner.IsValid
                ? $"Corner ({worldVertex.X:F2},{worldVertex.Y:F2}) · {bestCorner.IncludedAngleDeg:F3}°"
                : "Corner NG (직교 에지 미검출)";
            string detail = bestCorner.IsValid
                ? $"2-Rake 가상 코너=({worldVertex.X:F3}, {worldVertex.Y:F3}), Line1={line1AngleDeg:F3}°, Line2={line2AngleDeg:F3}°, 내각={bestCorner.IncludedAngleDeg:F3}° (직각편차={angleDevDeg:F3}°), 인라이어={bestInliers.Length}점 / 기각(모따기·결함)={bestOutliers.Count}점"
                : "유효한 직교 두 변 에지를 찾지 못했습니다 (내각 90°±5° 이탈 또는 인라이어 부족).";

            var contours = ImmutableArray.CreateBuilder<ImmutableArray<PointD>>();
            var primaryPts = ImmutableArray.CreateBuilder<PointD>(bestInliers.Length + 1);
            var outlierPts = ImmutableArray.CreateBuilder<PointD>(bestOutliers.Count);
            var rakeGuides = ImmutableArray.CreateBuilder<ImmutableArray<PointD>>();

            if (bestCorner.IsValid)
            {
                Vector2 cornerLocal = bestCorner.CornerPoint;

                // Line 1 연장 직선: 피팅된 헤세 법선 직선(Line1)을 모따기 바깥 허공의 가상 코너 교차점(cornerLocal)까지 정확히 연장
                Vector2 t1 = bestCorner.Line1.Tangent;
                if (Vector2.Dot(bestLeg1.EndPoint - cornerLocal, t1) < 0f)
                {
                    t1 = -t1;
                }
                Vector2 far1 = bestCorner.Line1.ProjectPoint(bestLeg1.EndPoint);
                Vector2 extStart1 = cornerLocal - (t1 * 8.0f);
                contours.Add([
                    new PointD(offsetX + extStart1.X, offsetY + extStart1.Y),
                    new PointD(offsetX + far1.X, offsetY + far1.Y)
                ]);

                // Line 2 연장 직선: 피팅된 헤세 법선 직선(Line2)을 모따기 바깥 허공의 가상 코너 교차점(cornerLocal)까지 정확히 연장
                Vector2 t2 = bestCorner.Line2.Tangent;
                if (Vector2.Dot(bestLeg2.EndPoint - cornerLocal, t2) < 0f)
                {
                    t2 = -t2;
                }
                Vector2 far2 = bestCorner.Line2.ProjectPoint(bestLeg2.EndPoint);
                Vector2 extStart2 = cornerLocal - (t2 * 8.0f);
                contours.Add([
                    new PointD(offsetX + extStart2.X, offsetY + extStart2.Y),
                    new PointD(offsetX + far2.X, offsetY + far2.Y)
                ]);

                primaryPts.Add(worldVertex);
                for (int i = 0; i < bestInliers.Length; i++)
                {
                    primaryPts.Add(new PointD(offsetX + bestInliers[i].X, offsetY + bestInliers[i].Y));
                }

                for (int i = 0; i < bestOutliers.Count; i++)
                {
                    outlierPts.Add(new PointD(offsetX + bestOutliers[i].X, offsetY + bestOutliers[i].Y));
                }

                AppendRakeGuideContours(bestLeg1, offsetX, offsetY, rakeGuides);
                AppendRakeGuideContours(bestLeg2, offsetX, offsetY, rakeGuides);
            }
            else
            {
                rakeGuides.AddRange(BuildGeometricGuidesForRoi(roi, InspectionSuiteAlgorithmKind.CornerAlignment, parameters));
            }

            results.Add(new InspectionSuiteItemResult(
                roi.RoiId,
                roi.Name,
                isPass,
                badge,
                detail,
                worldVertex,
                bestCorner.IsValid ? bestCorner.OrientationRad : roi.AngleRadians,
                roi.Outline,
                contours.ToImmutable(),
                primaryPts.ToImmutable(),
                [],
                outlierPts.ToImmutable(),
                us,
                rakeGuides.ToImmutable()));
        }

        return results.MoveToImmutable();
    }

    private static Vector2 EstimateCornerApexInRoi(
        ReadOnlyImageView2D<byte> view,
        Vector2 localCenter,
        Vector2 legDir1,
        Vector2 legDir2,
        float halfW,
        float halfH,
        float projWidth,
        CornerDetectionOptions options)
    {
        ReadOnlySpan<float> probeFractions = [0.35f, 0.58f, 0.80f];
        float sumV = 0f;
        int countV = 0;
        for (int i = 0; i < probeFractions.Length; i++)
        {
            Vector2 probeCenter = localCenter + (legDir1 * (halfW * probeFractions[i]));
            if (VisionAlgorithm.OpenSrc.CornerAlignment.EdgeRake.Simd1DProjectionCaliper.TryMeasureEdge(
                view,
                probeCenter,
                legDir2,
                halfH * 1.85f,
                projWidth,
                CaliperPolarity.Any,
                options.GaussianSigma,
                options.MinimumContrast,
                out SubpixelEdgePoint hit))
            {
                sumV += Vector2.Dot(hit.Position - localCenter, legDir2);
                countV++;
            }
        }

        float sumU = 0f;
        int countU = 0;
        for (int i = 0; i < probeFractions.Length; i++)
        {
            Vector2 probeCenter = localCenter + (legDir2 * (halfH * probeFractions[i]));
            if (VisionAlgorithm.OpenSrc.CornerAlignment.EdgeRake.Simd1DProjectionCaliper.TryMeasureEdge(
                view,
                probeCenter,
                legDir1,
                halfW * 1.85f,
                projWidth,
                CaliperPolarity.Any,
                options.GaussianSigma,
                options.MinimumContrast,
                out SubpixelEdgePoint hit))
            {
                sumU += Vector2.Dot(hit.Position - localCenter, legDir1);
                countU++;
            }
        }

        float estU = countU >= 2 ? (sumU / countU) : 0f;
        float estV = countV >= 2 ? (sumV / countV) : 0f;
        return localCenter + (legDir1 * estU) + (legDir2 * estV);
    }

    private static void CollectChamferRejectedPoints(
        ReadOnlyImageView2D<byte> view,
        Vector2 approxApex,
        Vector2 legDir1,
        Vector2 legDir2,
        float apexMargin1,
        float apexMargin2,
        float projWidth,
        CornerDetectionOptions options,
        List<SubpixelEdgePoint> outliersOut)
    {
        ReadOnlySpan<float> chamferProbes = [0.28f, 0.52f];
        for (int i = 0; i < chamferProbes.Length; i++)
        {
            float d1 = apexMargin1 * chamferProbes[i];
            Vector2 c1 = approxApex + (legDir1 * d1);
            if (!VisionAlgorithm.OpenSrc.CornerAlignment.EdgeRake.Simd1DProjectionCaliper.TryMeasureEdge(
                view, c1, legDir2, 28f, projWidth, CaliperPolarity.Any, options.GaussianSigma, options.MinimumContrast, out SubpixelEdgePoint hit1)
                && hit1.Magnitude >= options.MinimumContrast)
            {
                outliersOut.Add(hit1);
            }

            float d2 = apexMargin2 * chamferProbes[i];
            Vector2 c2 = approxApex + (legDir2 * d2);
            if (!VisionAlgorithm.OpenSrc.CornerAlignment.EdgeRake.Simd1DProjectionCaliper.TryMeasureEdge(
                view, c2, legDir1, 28f, projWidth, CaliperPolarity.Any, options.GaussianSigma, options.MinimumContrast, out SubpixelEdgePoint hit2)
                && hit2.Magnitude >= options.MinimumContrast)
            {
                outliersOut.Add(hit2);
            }
        }
    }

    private static void AppendRakeGuideContours(
        RakeSearchRegion region,
        int offsetX,
        int offsetY,
        ImmutableArray<ImmutableArray<PointD>>.Builder guides)
    {
        Vector2 legDir = region.GetUnitLegDirection(out float totalLen);
        Vector2 scanDir = region.GetUnitScanDirection();
        float startOffset = Math.Clamp(region.ApexIgnoreMargin, 0f, totalLen * 0.70f);
        float usableLen = MathF.Max(1f, totalLen - startOffset);
        Vector2 activeStart = region.StartPoint + (legDir * startOffset);
        Vector2 activeEnd = region.EndPoint;

        guides.Add([
            new PointD(offsetX + activeStart.X, offsetY + activeStart.Y),
            new PointD(offsetX + activeEnd.X, offsetY + activeEnd.Y)
        ]);

        int spokes = Math.Clamp(region.CaliperCount, 5, 16);
        float halfScan = region.SearchLength * 0.5f;
        for (int s = 0; s < spokes; s++)
        {
            float t = spokes == 1 ? 0.5f : (float)s / (spokes - 1);
            Vector2 center = activeStart + (legDir * (t * usableLen));
            Vector2 p0 = center - (scanDir * halfScan);
            Vector2 p1 = center + (scanDir * halfScan);
            guides.Add([
                new PointD(offsetX + p0.X, offsetY + p0.Y),
                new PointD(offsetX + p1.X, offsetY + p1.Y)
            ]);
        }
    }

    private const int MaxOcrRectifiedWidth = 4096;
    private const int MaxOcrRectifiedHeight = 2048;
    private const int OcrCropMarginPixels = 16;
    private const int DefaultOcrPadX = 6;
    private const int DefaultOcrPadY = 4;
    private const int MinRectifiedOcrWidth = 12;
    private const int MinRectifiedOcrHeight = 10;
    private const int QuadPolygonVertexCount = 4;
    private const float MinAcceptConfidenceClamp = 0.05f;
    private const float MaxAcceptConfidenceClamp = 1.0f;

    private readonly record struct RectifiedOcrSlot(
        InspectionSuiteRoiInput Roi,
        FixturePose2D Fixture,
        int OffsetX,
        int OffsetY,
        int RoiWidth,
        int RoiHeight,
        byte[] RentedPixels,
        double PrepMicroseconds);

    private static ImmutableArray<InspectionSuiteItemResult> RunPpOcrOnnx(
        MatchingImageReader reader,
        int imageWidth,
        int imageHeight,
        IReadOnlyList<InspectionSuiteRoiInput> rois,
        InspectionSuiteParameters parameters,
        IPpOcrInferenceService? ppOcrService,
        CancellationToken token)
    {
        var results = ImmutableArray.CreateBuilder<InspectionSuiteItemResult>(rois.Count);
        if (ppOcrService is null)
        {
            foreach (var roi in rois)
            {
                token.ThrowIfCancellationRequested();
                results.Add(new InspectionSuiteItemResult(
                    roi.RoiId,
                    roi.Name,
                    IsPass: false,
                    BadgeLabel: "모델 미연결",
                    DetailLine: "PP-OCR (ONNX) 서비스가 연결되지 않았습니다.",
                    roi.Center,
                    roi.AngleRadians,
                    roi.Outline,
                    [],
                    [],
                    [],
                    [roi.Center],
                    ElapsedMicroseconds: 0.0,
                    BuildGeometricGuidesForRoi(roi, InspectionSuiteAlgorithmKind.PpOcrOnnx, parameters),
                    OcrDiagnostic: null));
            }

            return results.MoveToImmutable();
        }

        float minAccept = Math.Clamp(parameters.PpOcrMinConfidence, MinAcceptConfidenceClamp, MaxAcceptConfidenceClamp);
        float minChar = Math.Clamp(parameters.PpOcrMinCharacterConfidence, 0.0f, MaxAcceptConfidenceClamp);

        PpOcrRunOptions runOptions = parameters.PpOcrPreset == PpOcrSpeedPreset.HighSpeedProduction
            ? PpOcrRunOptions.CreateHighSpeedProduction(
                language: parameters.PpOcrLanguageMode,
                polarityMode: parameters.PpOcrPolarity,
                allowedCharacterSet: parameters.OcrAllowedCharacters,
                minAcceptConfidence: minAccept,
                minCharacterConfidence: minChar)
            : new PpOcrRunOptions(
                Language: parameters.PpOcrLanguageMode,
                PipelineMode: parameters.PpOcrPipeline,
                PolarityMode: parameters.PpOcrPolarity,
                AllowedCharacterSet: parameters.OcrAllowedCharacters,
                MinAcceptConfidence: minAccept,
                MinCharacterConfidence: minChar,
                FastInteractivePreview: parameters.IsInteractivePreview);

        using var visionWs = new VisionWorkspace();
        var slots = new List<RectifiedOcrSlot>(rois.Count);

        try
        {
            var patches = new PpOcrRoiPatch[rois.Count];
            for (int i = 0; i < rois.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                InspectionSuiteRoiInput roi = rois[i];
                var prepSw = Stopwatch.StartNew();
                var (crop, localCenter, offsetX, offsetY) = ReadLocalCrop(reader, imageWidth, imageHeight, roi, marginPixels: OcrCropMarginPixels, token);
                var cropView = new ReadOnlyImageView2D<byte>(crop.Pixels.Span, crop.Width, crop.Height, crop.Stride);
                var fixture = new FixturePose2D(localCenter.X, localCenter.Y, (float)roi.AngleRadians);

                int roiW = Math.Clamp((int)Math.Round(roi.Width) + DefaultOcrPadX, MinRectifiedOcrWidth, Math.Min(MaxOcrRectifiedWidth, cropView.Width));
                int roiH = Math.Clamp((int)Math.Round(roi.Height) + DefaultOcrPadY, MinRectifiedOcrHeight, Math.Min(MaxOcrRectifiedHeight, cropView.Height));
                int pixelCount = checked(roiW * roiH);

                byte[] rented = ArrayPool<byte>.Shared.Rent(pixelCount);
                Span<byte> rectifiedRoi = rented.AsSpan(0, pixelCount);
                Span<float> warpScratch = visionWs.GetFloatBufferA(pixelCount);
                SimdAffineWarper.WarpAffineBilinear(cropView, in fixture, roiW, roiH, rectifiedRoi, warpScratch);
                prepSw.Stop();

                slots.Add(new RectifiedOcrSlot(roi, fixture, offsetX, offsetY, roiW, roiH, rented, prepSw.Elapsed.TotalMicroseconds));
                patches[i] = new PpOcrRoiPatch(rented.AsMemory(0, pixelCount), roiW, roiH, roiW);
            }

            token.ThrowIfCancellationRequested();
            var batchSw = Stopwatch.StartNew();
            IReadOnlyList<PpOcrRecognitionResult> batchOutputs = ppOcrService.RecognizeBatch(patches, runOptions);
            batchSw.Stop();

            double amortizedBatchMicroseconds = batchOutputs.Count > 0
                ? batchSw.Elapsed.TotalMicroseconds / batchOutputs.Count
                : 0.0;

            for (int i = 0; i < slots.Count; i++)
            {
                RectifiedOcrSlot slot = slots[i];
                InspectionSuiteRoiInput roi = slot.Roi;
                PpOcrRecognitionResult ppRes = batchOutputs[i];
                double itemElapsedUs = slot.PrepMicroseconds + amortizedBatchMicroseconds;

                if (ppRes.ModelStatus != PpOcrModelStatus.Ready)
                {
                    results.Add(new InspectionSuiteItemResult(
                        roi.RoiId,
                        roi.Name,
                        IsPass: false,
                        BadgeLabel: "모델 미설치",
                        DetailLine: $"{ppRes.StatusMessage} (가짜 판독 결과 미반환)",
                        roi.Center,
                        roi.AngleRadians,
                        roi.Outline,
                        [],
                        [],
                        [],
                        [roi.Center],
                        itemElapsedUs,
                        BuildGeometricGuidesForRoi(roi, InspectionSuiteAlgorithmKind.PpOcrOnnx, parameters),
                        OcrDiagnostic: null));
                    continue;
                }

                var roiView = new ReadOnlyImageView2D<byte>(
                    slot.RentedPixels.AsSpan(0, slot.RoiWidth * slot.RoiHeight),
                    slot.RoiWidth,
                    slot.RoiHeight,
                    slot.RoiWidth);

                UniversalOcrRegionResult ocrRes = ppRes.Region;
                float halfW = (slot.RoiWidth - 1) * 0.5f;
                float halfH = (slot.RoiHeight - 1) * 0.5f;
                var charContours = ImmutableArray.CreateBuilder<ImmutableArray<PointD>>(ocrRes.Characters.Count + ppRes.Boxes.Length);
                var charCenters = ImmutableArray.CreateBuilder<PointD>(ocrRes.Characters.Count + 1);
                var lowConfCenters = ImmutableArray.CreateBuilder<PointD>();
                charCenters.Add(roi.Center);

                foreach (PpOcrBoxResult detBox in ppRes.Boxes)
                {
                    if (detBox.Polygon.Length == QuadPolygonVertexCount)
                    {
                        var worldPoly = ImmutableArray.CreateBuilder<PointD>(QuadPolygonVertexCount);
                        for (int p = 0; p < QuadPolygonVertexCount; p++)
                        {
                            var wp = slot.Fixture.LocalToWorld((float)detBox.Polygon[p].X - halfW, (float)detBox.Polygon[p].Y - halfH);
                            worldPoly.Add(new PointD(slot.OffsetX + wp.X, slot.OffsetY + wp.Y));
                        }

                        charContours.Add(worldPoly.MoveToImmutable());
                    }
                }

                foreach (RecognizedCharBox ch in ocrRes.Characters)
                {
                    float lx0 = ch.StartCol - halfW;
                    float ly0 = ch.TopRow - halfH;
                    float lx1 = ch.EndCol - halfW;
                    float ly1 = ch.BottomRow - halfH;
                    var pc = slot.Fixture.LocalToWorld(0.5f * (lx0 + lx1), 0.5f * (ly0 + ly1));
                    PointD worldPt = new(slot.OffsetX + pc.X, slot.OffsetY + pc.Y);
                    if (ch.Confidence >= runOptions.MinCharacterConfidence && ch.Character != '?')
                    {
                        charCenters.Add(worldPt);
                    }
                    else
                    {
                        lowConfCenters.Add(worldPt);
                    }
                }

                string singleLineDisplay = ocrRes.FullText.Replace("\n", " / ", StringComparison.Ordinal);
                string polarityTag = ppRes.UsedInvertedPolarity ? "LightOnDark(반전)" : "DarkOnLight(일반)";
                string stageTag = ppRes.UsedDetectionStage ? "Det+Rec" : "RecOnly";
                string batchTag = slots.Count > 1 ? $"·배치x{slots.Count}" : string.Empty;
                bool isPass = ocrRes.IsPass && !string.IsNullOrWhiteSpace(ocrRes.FullText) && lowConfCenters.Count == 0;

                string badge = string.IsNullOrWhiteSpace(singleLineDisplay)
                    ? "OCR N/A"
                    : $"'{singleLineDisplay}' · P={ocrRes.MeanConfidence:F2}";

                string detail = $"OCR({ppRes.ActiveDevice}·{stageTag}{batchTag})='{singleLineDisplay}', 합격={isPass}, 평균확률={ocrRes.MeanConfidence:F3}, 최소확률={ocrRes.MinConfidence:F3}, 줄수={ocrRes.Lines.Count}, 문자수={ocrRes.Characters.Count}개, 극성={polarityTag}";
                OcrDiagnosticSnapshot diag = OcrDiagnosticPreviewBuilder.Build(
                    roiView, ocrRes, InspectionSuiteAlgorithmKind.PpOcrOnnx, parameters);

                results.Add(new InspectionSuiteItemResult(
                    roi.RoiId,
                    roi.Name,
                    isPass,
                    badge,
                    detail,
                    roi.Center,
                    roi.AngleRadians,
                    roi.Outline,
                    charContours.ToImmutable(),
                    charCenters.ToImmutable(),
                    [],
                    lowConfCenters.ToImmutable(),
                    itemElapsedUs,
                    [],
                    diag));
            }

            return results.MoveToImmutable();
        }
        finally
        {
            for (int i = 0; i < slots.Count; i++)
            {
                ArrayPool<byte>.Shared.Return(slots[i].RentedPixels);
            }
        }
    }

    private static ImmutableArray<InspectionSuiteItemResult> RunAutoTeaching(
        MatchingImageReader reader,
        int imageWidth,
        int imageHeight,
        IReadOnlyList<InspectionSuiteRoiInput> rois,
        IReadOnlyList<InspectionSuiteRoiInput> referenceRois,
        InspectionSuiteParameters parameters,
        InspectionWorkspace workspace,
        CancellationToken token)
    {
        _ = workspace;
        using var visionWs = new VisionWorkspace();
        token.ThrowIfCancellationRequested();
        var sw = Stopwatch.StartNew();
        var fullImage = reader.Read(
            0,
            0,
            imageWidth,
            imageHeight,
            token,
            maximumPixels: VisionAlgorithm.OpenSrc.PatternMatching.CorrelationTemplate.MaximumSearchPixels);
        var fullView = new ReadOnlyImageView2D<byte>(fullImage.Pixels.Span, fullImage.Width, fullImage.Height, fullImage.Stride);

        using var pool = new PartModelPool();
        var childRoisA = new[]
        {
            new ChildRoiTemplateSpec("HoleCheck_A", ChildToolKind.SubpixelCaliperWidth, new FixturePose2D(0f, 0f, 0f), 120f, 120f),
            new ChildRoiTemplateSpec("KeywayTab_A", ChildToolKind.FiducialCrosshairCheck, new FixturePose2D(42f, 0f, 0f), 40f, 24f)
        };
        var childRoisB = new[]
        {
            new ChildRoiTemplateSpec("FrameCheck_B", ChildToolKind.SubpixelCaliperWidth, new FixturePose2D(0f, 0f, 0f), 124f, 84f),
            new ChildRoiTemplateSpec("Pin1Dot_B", ChildToolKind.GoldenDifferenceAnomaly, new FixturePose2D(-38f, -20f, 0f), 24f, 24f)
        };
        var childRoisC = new[]
        {
            new ChildRoiTemplateSpec("CrossFiducial_C", ChildToolKind.FiducialCrosshairCheck, new FixturePose2D(0f, 0f, 0f), 104f, 104f)
        };

        // 1) 표준 3종 파츠 골든 마스터 모델 등록
        byte[] g1 = new byte[512 * 512];
        byte[] g2 = new byte[512 * 512];
        byte[] g3 = new byte[512 * 512];
        Array.Fill(g1, (byte)232);
        Array.Fill(g2, (byte)232);
        Array.Fill(g3, (byte)232);
        UnifiedTestImageSuiteGenerator.RenderPartIntoBuffer(g1, 512, 512, 512, 1, 256f, 256f, 0f);
        UnifiedTestImageSuiteGenerator.RenderPartIntoBuffer(g2, 512, 512, 512, 2, 256f, 256f, 0f);
        UnifiedTestImageSuiteGenerator.RenderPartIntoBuffer(g3, 512, 512, 512, 3, 256f, 512 * 0.5f, 0f);
        var centerPose = new FixturePose2D(256f, 256f, 0f);
        pool.RegisterOrMergeOnTeach(new ReadOnlyImageView2D<byte>(g1, 512, 512, 512), in centerPose, 200f, 200f, "Part#1_Bracket", childRoisA, visionWs);
        pool.RegisterOrMergeOnTeach(new ReadOnlyImageView2D<byte>(g2, 512, 512, 512), in centerPose, 200f, 200f, "Part#2_Connector", childRoisB, visionWs);
        pool.RegisterOrMergeOnTeach(new ReadOnlyImageView2D<byte>(g3, 512, 512, 512), in centerPose, 200f, 200f, "Part#3_ShieldCan", childRoisC, visionWs);

        // 2) 사용자가 캔버스에서 직접 학습(티칭) 등록한 파츠 ROI가 있으면 모델 풀에 추가 학습 또는 기존 모델과 하위 ROI 자동 병합
        for (int r = 0; r < referenceRois.Count; r++)
        {
            var refRoi = referenceRois[r];
            var refPose = new FixturePose2D((float)refRoi.Center.X, (float)refRoi.Center.Y, (float)refRoi.AngleRadians);
            var customChildren = new[]
            {
                new ChildRoiTemplateSpec($"Child_{r + 1}_Center", ChildToolKind.SubpixelCaliperWidth, new FixturePose2D(0f, 0f, 0f), (float)(refRoi.Width * 0.55), (float)(refRoi.Height * 0.55)),
                new ChildRoiTemplateSpec($"Child_{r + 1}_Lead", ChildToolKind.FiducialCrosshairCheck, new FixturePose2D((float)(refRoi.Width * 0.22), 0f, 0f), (float)(refRoi.Width * 0.20), (float)(refRoi.Height * 0.16))
            };
            pool.RegisterOrMergeOnTeach(
                fullView,
                in refPose,
                MathF.Max(32f, (float)refRoi.Width),
                MathF.Max(32f, (float)refRoi.Height),
                string.IsNullOrWhiteSpace(refRoi.Name) ? $"UserPart#{r + 1}" : refRoi.Name,
                customChildren,
                visionWs);
        }

        AutoTeachingResult autoRes = AutoTeachingOrchestrator.RecognizeAndAutoTeach(
            fullView,
            pool,
            visionWs,
            minGeometricScore: Math.Clamp(parameters.AutoTeachMinGeometricScore, 0.10f, 0.98f),
            minAppearanceCosine: Math.Clamp(parameters.AutoTeachMinAppearanceCosine, 0.10f, 0.98f),
            maxMahalanobisDistance: Math.Clamp(parameters.AutoTeachMaxMahalanobis, 3.0f, 100.0f));
        sw.Stop();

        var results = ImmutableArray.CreateBuilder<InspectionSuiteItemResult>(Math.Max(1, autoRes.Instances.Length));
        for (int i = 0; i < autoRes.Instances.Length; i++)
        {
            DetectedPartInstance inst = autoRes.Instances[i];
            PointD pt = new(inst.WorldPose.OriginX, inst.WorldPose.OriginY);
            var childContours = ImmutableArray.CreateBuilder<ImmutableArray<PointD>>(inst.ChildRois.Length);
            var childCenters = ImmutableArray.CreateBuilder<PointD>(inst.ChildRois.Length);
            foreach (var child in inst.ChildRois)
            {
                PointD cc = new(child.WorldCenter.X, child.WorldCenter.Y);
                childCenters.Add(cc);
                childContours.Add(BuildCirclePolygon(cc, Math.Min(child.Width, child.Height) * 0.4, 20));
            }

            results.Add(new InspectionSuiteItemResult(
                Guid.NewGuid(),
                $"{inst.PartId} ({inst.PartName})",
                true,
                $"{inst.PartId} · Score={inst.GeometricScore:F2} · 하위ROI={inst.ChildRois.Length}개",
                $"자동 인식={inst.PartName} ({inst.PartId}), 중심=({pt.X:F2},{pt.Y:F2}), 각도={inst.WorldPose.AngleRad * 180.0 / Math.PI:F2}°, 하위 ROI {inst.ChildRois.Length}개 월드 좌표 자동 생성 완료 ({autoRes.SummaryText})",
                pt,
                inst.WorldPose.AngleRad,
                BuildCirclePolygon(pt, 90.0, 32),
                childContours.MoveToImmutable(),
                [pt],
                childCenters.MoveToImmutable(),
                [],
                sw.Elapsed.TotalMicroseconds / Math.Max(1, autoRes.Instances.Length)));
        }

        if (results.Count == 0 && rois.Count > 0)
        {
            results.Add(CreateZoneMarker("AutoTeaching", rois[0].Center, rois[0].Width, rois[0].Height, false, "인식 파츠 0개", "화면에서 등록된 파츠 모델을 찾지 못했습니다."));
        }

        return results.MoveToImmutable();
    }

    private static ImmutableArray<InspectionSuiteItemResult> RunPreprocessingShadingAndRecon(
        MatchingImageReader reader,
        int imageWidth,
        int imageHeight,
        IReadOnlyList<InspectionSuiteRoiInput> targets,
        InspectionSuiteParameters parameters,
        InspectionWorkspace workspace,
        CancellationToken token)
    {
        var results = ImmutableArray.CreateBuilder<InspectionSuiteItemResult>(targets.Count);
        byte targetBase = (byte)Math.Clamp(parameters.ShadingTargetBaseGray, 60, 220);
        int markerRadius = Math.Clamp(parameters.ReconstructionMarkerRadius, 1, 16);
        float kappa = Math.Clamp(parameters.AnisotropicDiffusionKappa, 4.0f, 80.0f);

        for (int i = 0; i < targets.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var target = targets[i];
            long startTick = Stopwatch.GetTimestamp();

            var (crop, _, offsetX, offsetY) = ReadLocalCrop(reader, imageWidth, imageHeight, target, marginPixels: 0, token);
            var srcView = new ReadOnlyImageView2D<byte>(crop.Pixels.Span, crop.Width, crop.Height, crop.Stride);
            int totalPixels = checked(crop.Width * crop.Height);
            byte[] dstBuf = new byte[totalPixels];
            byte[] stageBuf = new byte[totalPixels];

            bool isDiffusionHint = target.Name.Contains("확산", StringComparison.OrdinalIgnoreCase)
                || target.Name.Contains("Diffusion", StringComparison.OrdinalIgnoreCase);
            bool isReconHint = target.Name.Contains("재구성", StringComparison.OrdinalIgnoreCase)
                || target.Name.Contains("Recon", StringComparison.OrdinalIgnoreCase)
                || target.Name.Contains("핀홀", StringComparison.OrdinalIgnoreCase);

            string modeTag;
            if (isDiffusionHint)
            {
                var diffSpec = AnisotropicDiffusionSpec.Create(iterations: 10, kappaContrast: kappa, timeStepLambda: 0.20f);
                ShadingAndReconstructionEngine.ApplyAnisotropicDiffusion(srcView, in diffSpec, dstBuf, crop.Width);
                modeTag = $"Perona-Malik 비등방성 확산 (κ={kappa:F1}, Iter=10)";
            }
            else if (isReconHint)
            {
                var reconSpec = MorphologicalReconstructionSpec.Create(markerRadius: markerRadius, isFillDarkHoles: false);
                ShadingAndReconstructionEngine.ApplyMorphologicalReconstruction(srcView, in reconSpec, dstBuf, crop.Width);
                modeTag = $"Vincent 형태학적 재구성 (R={markerRadius}px)";
            }
            else
            {
                var shadingSpec = PolynomialShadingSpec.Create(targetBaseGray: targetBase, isMultiplicativeGain: false, subsampleStep: 2);
                ShadingAndReconstructionEngine.ApplyPolynomialShadingCorrection(srcView, in shadingSpec, stageBuf, crop.Width);
                var reconSpec = MorphologicalReconstructionSpec.Create(markerRadius: Math.Max(2, markerRadius), isFillDarkHoles: false);
                ShadingAndReconstructionEngine.ApplyMorphologicalReconstruction(
                    new ReadOnlyImageView2D<byte>(stageBuf, crop.Width, crop.Height, crop.Width),
                    in reconSpec,
                    dstBuf,
                    crop.Width);
                modeTag = $"2D 다항식 조명 평탄화(Base={targetBase}) + 재구성(R={Math.Max(2, markerRadius)})";
            }

            ComputeGraySpreadStats(srcView, out float rawStd, out float rawGradientRange);
            var outView = new ReadOnlyImageView2D<byte>(dstBuf, crop.Width, crop.Height, crop.Width);
            ComputeGraySpreadStats(outView, out float cleanStd, out float cleanGradientRange);

            RleRegion postRegion = RleRegion.Threshold(outView, minIntensity: (byte)Math.Clamp(targetBase + 22, 100, 245), maxIntensity: 255);
            List<BlobComponentFeature> postBlobs = RleBlobAnalyzer.AnalyzeComponents(postRegion, minimumAreaPixels: Math.Max(4, parameters.BlobMinimumAreaPixels), workspace);

            var primaryPts = ImmutableArray.CreateBuilder<PointD>(postBlobs.Count + 1);
            var contours = ImmutableArray.CreateBuilder<ImmutableArray<PointD>>(postBlobs.Count);
            for (int b = 0; b < postBlobs.Count; b++)
            {
                var blob = postBlobs[b];
                primaryPts.Add(new PointD(blob.Centroid.X + offsetX, blob.Centroid.Y + offsetY));
                double l = blob.MinCol + offsetX;
                double t = blob.MinRow + offsetY;
                double r = l + blob.BoundingWidth;
                double btm = t + blob.BoundingHeight;
                contours.Add([new PointD(l, t), new PointD(r, t), new PointD(r, btm), new PointD(l, btm)]);
            }

            if (primaryPts.Count == 0)
            {
                primaryPts.Add(target.Center);
            }

            double elapsedUs = (Stopwatch.GetTimestamp() - startTick) * 1_000_000.0 / Stopwatch.Frequency;
            bool isPass = cleanGradientRange <= rawGradientRange + 2.0f;
            string badge = $"배경편차 {rawGradientRange:F1}→{cleanGradientRange:F1}GV · 검출={postBlobs.Count}개";
            string detail = $"{modeTag} 완료 · 배경 구배 편차 {rawGradientRange:F1}→{cleanGradientRange:F1} GV (전역 σ {rawStd:F1}→{cleanStd:F1}) · 전처리 후 유효 객체 {postBlobs.Count}개 분리";

            results.Add(new InspectionSuiteItemResult(
                target.RoiId,
                target.Name,
                isPass,
                badge,
                detail,
                target.Center,
                target.AngleRadians,
                target.Outline,
                contours.ToImmutable(),
                primaryPts.ToImmutable(),
                [],
                [],
                elapsedUs,
                BuildGeometricGuidesForRoi(target, InspectionSuiteAlgorithmKind.PreprocessingShadingAndRecon, parameters)));
        }

        return results.MoveToImmutable();
    }

    private static void ComputeGraySpreadStats(ReadOnlyImageView2D<byte> view, out float stdDev, out float quadrantSpread)
    {
        int w = view.Width;
        int h = view.Height;
        int halfW = Math.Max(1, w / 2);
        int halfH = Math.Max(1, h / 2);

        double sum = 0.0;
        double sumSq = 0.0;
        Span<double> qSum = stackalloc double[4];
        Span<int> qCount = stackalloc int[4];
        qSum.Clear();
        qCount.Clear();

        for (int y = 0; y < h; y++)
        {
            ReadOnlySpan<byte> row = view.GetRowSpan(y);
            int qy = y < halfH ? 0 : 2;
            for (int x = 0; x < w; x++)
            {
                byte v = row[x];
                sum += v;
                sumSq += v * v;
                int qIdx = qy + (x < halfW ? 0 : 1);
                qSum[qIdx] += v;
                qCount[qIdx]++;
            }
        }

        int total = w * h;
        double mean = sum / total;
        stdDev = (float)Math.Sqrt(Math.Max(0.0, (sumSq / total) - (mean * mean)));

        double minQ = double.MaxValue;
        double maxQ = double.MinValue;
        for (int q = 0; q < 4; q++)
        {
            if (qCount[q] > 0)
            {
                double qMean = qSum[q] / qCount[q];
                if (qMean < minQ) minQ = qMean;
                if (qMean > maxQ) maxQ = qMean;
            }
        }

        quadrantSpread = maxQ >= minQ ? (float)(maxQ - minQ) : 0.0f;
    }

    private static ImmutableArray<InspectionSuiteItemResult> RunAdvancedMetrologyEllipse(
        MatchingImageReader reader,
        int imageWidth,
        int imageHeight,
        IReadOnlyList<InspectionSuiteRoiInput> targets,
        InspectionSuiteParameters parameters,
        InspectionWorkspace workspace,
        CancellationToken token)
    {
        var results = ImmutableArray.CreateBuilder<InspectionSuiteItemResult>(targets.Count);
        int projStrips = Math.Clamp((int)Math.Round(parameters.CaliperProjectionWidth), 3, 45);
        float sigma = Math.Clamp(parameters.GaussianSigma, 0.5f, 8.0f);
        float minContrast = Math.Clamp(parameters.MinimumContrast, 4.0f, 180.0f);
        int spokeCount = Math.Clamp(parameters.EllipseSpokeCount, 12, 48);
        float maxEllipseRms = Math.Clamp(parameters.EllipseMaxRmsErrorPx, 0.2f, 5.0f);
        float userExpectedSpan = Math.Clamp(parameters.CompositeExpectedSpanPx, 6.0f, 160.0f);

        for (int i = 0; i < targets.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var target = targets[i];
            long startTick = Stopwatch.GetTimestamp();

            var (crop, localCenter, offsetX, offsetY) = ReadLocalCrop(reader, imageWidth, imageHeight, target, marginPixels: 28, token);
            var view = new ReadOnlyImageView2D<byte>(crop.Pixels.Span, crop.Width, crop.Height, crop.Stride);
            var localPose = new FixturePose2D(localCenter.X, localCenter.Y, (float)target.AngleRadians, 1.0f);

            bool isCaliperHint = target.Name.Contains("캘리퍼", StringComparison.OrdinalIgnoreCase)
                || target.Name.Contains("Caliper", StringComparison.OrdinalIgnoreCase)
                || target.Name.Contains("복합", StringComparison.OrdinalIgnoreCase);

            var primaryPts = ImmutableArray.CreateBuilder<PointD>();
            var secondaryPts = ImmutableArray.CreateBuilder<PointD>();
            var contours = ImmutableArray.CreateBuilder<ImmutableArray<PointD>>();

            if (isCaliperHint)
            {
                float scanLen = MathF.Max(24.0f, (float)target.Width * 0.90f);
                float projWidth = Math.Clamp(parameters.CaliperProjectionWidth, 3.0f, MathF.Max(5.0f, (float)target.Height * 0.80f));
                float expectedSpan = Math.Abs(userExpectedSpan - 28.0f) > 0.5f
                    ? userExpectedSpan
                    : MathF.Max(14.0f, (float)target.Width * 0.34f);

                var compSpec = CompositeCaliperSpec.Create(
                    selectionMode: CaliperSelectionMode.CompositeScore,
                    gaussianSigma: sigma,
                    minimumContrast: minContrast,
                    projectionStripCount: projStrips,
                    expectedPosition: 0.0f,
                    expectedSpan: expectedSpan,
                    positionToleranceSigma: scanLen * 0.22f,
                    spanToleranceSigma: expectedSpan * 0.30f);

                CaliperPairResult pair = CompositeSubpixelCaliper.MeasureEdgePair(
                    view,
                    localPose,
                    scanLen,
                    projWidth,
                    CaliperEdgePolarity.DarkToLight,
                    CaliperEdgePolarity.LightToDark,
                    in compSpec,
                    workspace);

                var firstEdgeSpec = CompositeCaliperSpec.Create(CaliperSelectionMode.FirstEdge, sigma, minContrast, projStrips);
                var lastEdgeSpec = CompositeCaliperSpec.Create(CaliperSelectionMode.LastEdge, sigma, minContrast, projStrips);
                CaliperEdgeHit firstHit = CompositeSubpixelCaliper.MeasureSingleEdge(view, localPose, scanLen, projWidth, CaliperEdgePolarity.Any, in firstEdgeSpec, workspace);
                CaliperEdgeHit lastHit = CompositeSubpixelCaliper.MeasureSingleEdge(view, localPose, scanLen, projWidth, CaliperEdgePolarity.Any, in lastEdgeSpec, workspace);

                if (pair.IsFound)
                {
                    PointD p1 = new(pair.FirstEdge.WorldPoint.X + offsetX, pair.FirstEdge.WorldPoint.Y + offsetY);
                    PointD p2 = new(pair.SecondEdge.WorldPoint.X + offsetX, pair.SecondEdge.WorldPoint.Y + offsetY);
                    primaryPts.Add(p1);
                    primaryPts.Add(p2);
                    contours.Add([p1, p2]);
                }

                if (firstHit.GradientAmplitude > 0)
                {
                    secondaryPts.Add(new PointD(firstHit.WorldPoint.X + offsetX, firstHit.WorldPoint.Y + offsetY));
                }
                if (lastHit.GradientAmplitude > 0)
                {
                    secondaryPts.Add(new PointD(lastHit.WorldPoint.X + offsetX, lastHit.WorldPoint.Y + offsetY));
                }

                double elapsedUs = (Stopwatch.GetTimestamp() - startTick) * 1_000_000.0 / Stopwatch.Frequency;
                bool isPass = pair.IsFound;
                string badge = isPass
                    ? $"복합캘리퍼 폭={pair.SubpixelWidth:F3}px (First/Last 억제)"
                    : "복합캘리퍼 미검출";
                string detail = isPass
                    ? $"Composite 스코어링 폭={pair.SubpixelWidth:F3}px (목표폭={expectedSpan:F1}px, 좌/우 대비={pair.FirstEdge.GradientAmplitude:F1}/{pair.SecondEdge.GradientAmplitude:F1}) · 외곽 노이즈 에지 First u={firstHit.SubpixelPosition:F1}, Last u={lastHit.SubpixelPosition:F1} 배제 완료"
                    : "복합 스코어링 조건에 부합하는 에지 쌍을 찾지 못했습니다.";

                results.Add(new InspectionSuiteItemResult(
                    target.RoiId,
                    target.Name,
                    isPass,
                    badge,
                    detail,
                    target.Center,
                    target.AngleRadians,
                    target.Outline,
                    contours.ToImmutable(),
                    primaryPts.ToImmutable(),
                    secondaryPts.ToImmutable(),
                    [],
                    elapsedUs,
                    BuildGeometricGuidesForRoi(target, InspectionSuiteAlgorithmKind.AdvancedMetrologyEllipse, parameters)));
            }
            else
            {
                float nomA = MathF.Max(12.0f, (float)target.Width * 0.36f);
                float nomB = MathF.Max(10.0f, (float)target.Height * 0.30f);
                float radialScan = MathF.Max(20.0f, MathF.Max(nomA, nomB) * 0.72f);
                var calSpec = new CaliperMeasurementSpec(sigma, minContrast, projStrips);

                FittedEllipse2D ellipse = FitzgibbonEllipseFitter.MeasureSpokeEllipse(
                    view,
                    localCenter,
                    nomA,
                    nomB,
                    (float)target.AngleRadians,
                    radialScan,
                    spokeCount,
                    CaliperEdgePolarity.Any,
                    calSpec,
                    workspace);

                if (ellipse.IsValid)
                {
                    PointD worldCenter = new(ellipse.Center.X + offsetX, ellipse.Center.Y + offsetY);
                    primaryPts.Add(worldCenter);
                    contours.Add(BuildEllipsePolygon(worldCenter, ellipse.SemiMajorAxis, ellipse.SemiMinorAxis, ellipse.AngleRad, 56));

                    (double sinE, double cosE) = Math.SinCos(ellipse.AngleRad);
                    contours.Add([
                        new PointD(worldCenter.X - (ellipse.SemiMajorAxis * cosE), worldCenter.Y - (ellipse.SemiMajorAxis * sinE)),
                        new PointD(worldCenter.X + (ellipse.SemiMajorAxis * cosE), worldCenter.Y + (ellipse.SemiMajorAxis * sinE))
                    ]);
                    contours.Add([
                        new PointD(worldCenter.X + (ellipse.SemiMinorAxis * sinE), worldCenter.Y - (ellipse.SemiMinorAxis * cosE)),
                        new PointD(worldCenter.X - (ellipse.SemiMinorAxis * sinE), worldCenter.Y + (ellipse.SemiMinorAxis * cosE))
                    ]);
                }

                double elapsedUs = (Stopwatch.GetTimestamp() - startTick) * 1_000_000.0 / Stopwatch.Frequency;
                bool isPass = ellipse.IsValid && ellipse.RmsGeometricErrorPixels < maxEllipseRms;
                double angleDeg = ellipse.AngleRad * (180.0 / Math.PI);
                string badge = ellipse.IsValid
                    ? $"타원 A={ellipse.SemiMajorAxis:F2} · B={ellipse.SemiMinorAxis:F2}px · θ={angleDeg:F1}°"
                    : "Fitzgibbon 타원 미검출";
                string detail = ellipse.IsValid
                    ? $"Fitzgibbon + Sampson Gauss-Newton 타원 피팅: 중심=({ellipse.Center.X + offsetX:F2},{ellipse.Center.Y + offsetY:F2}), 장축 2A={ellipse.MajorDiameter:F2}px, 단축 2B={ellipse.MinorDiameter:F2}px, 회전={angleDeg:F2}°, 인라이어={ellipse.InlierCount}/{spokeCount}, 기하 RMS={ellipse.RmsGeometricErrorPixels:F4}px"
                    : "방사형 스포크에서 유효한 타원 경계 에지를 충분히 검출하지 못했습니다.";

                results.Add(new InspectionSuiteItemResult(
                    target.RoiId,
                    target.Name,
                    isPass,
                    badge,
                    detail,
                    target.Center,
                    ellipse.IsValid ? ellipse.AngleRad : target.AngleRadians,
                    target.Outline,
                    contours.ToImmutable(),
                    primaryPts.ToImmutable(),
                    secondaryPts.ToImmutable(),
                    [],
                    elapsedUs,
                    BuildGeometricGuidesForRoi(target, InspectionSuiteAlgorithmKind.AdvancedMetrologyEllipse, parameters)));
            }
        }

        return results.MoveToImmutable();
    }

    private static ImmutableArray<PointD> BuildEllipsePolygon(PointD center, double a, double b, double angleRad, int segments)
    {
        var pts = ImmutableArray.CreateBuilder<PointD>(segments);
        (double sinR, double cosR) = Math.SinCos(angleRad);
        for (int i = 0; i < segments; i++)
        {
            double t = 2.0 * Math.PI * i / segments;
            double lx = a * Math.Cos(t);
            double ly = b * Math.Sin(t);
            pts.Add(new PointD(
                center.X + (lx * cosR) - (ly * sinR),
                center.Y + (lx * sinR) + (ly * cosR)));
        }

        return pts.MoveToImmutable();
    }

    private static ImmutableArray<InspectionSuiteItemResult> RunAdvancedBlobWatershed(
        MatchingImageReader reader,
        int imageWidth,
        int imageHeight,
        IReadOnlyList<InspectionSuiteRoiInput> targets,
        InspectionSuiteParameters parameters,
        InspectionWorkspace workspace,
        CancellationToken token)
    {
        var results = ImmutableArray.CreateBuilder<InspectionSuiteItemResult>(targets.Count);
        byte minThresh = (byte)Math.Clamp(parameters.WatershedThresholdIntensity, 1, 255);
        long minArea = Math.Clamp(parameters.WatershedMinAreaPixels, 4, 10000);
        float minPeakDist = Math.Clamp(parameters.WatershedMinPeakDistancePx, 4.0f, 48.0f);

        for (int i = 0; i < targets.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var target = targets[i];
            long startTick = Stopwatch.GetTimestamp();

            var (crop, _, offsetX, offsetY) = ReadLocalCrop(reader, imageWidth, imageHeight, target, marginPixels: 0, token);
            var grayView = new ReadOnlyImageView2D<byte>(crop.Pixels.Span, crop.Width, crop.Height, crop.Stride);
            RleRegion region = RleRegion.Threshold(grayView, minThresh, byte.MaxValue);

            bool useWatershed = target.Name.Contains("워터셰드", StringComparison.OrdinalIgnoreCase)
                || target.Name.Contains("Watershed", StringComparison.OrdinalIgnoreCase)
                || target.Name.Contains("접촉", StringComparison.OrdinalIgnoreCase)
                || target.Name.Contains("분리", StringComparison.OrdinalIgnoreCase);

            List<AdvancedBlobFeature> blobs = useWatershed
                ? AdvancedRleBlobExtractor.SeparateTouchingBlobsWatershed(region, grayView, minPeakSuppressionDistancePixels: minPeakDist, minimumAreaPixels: minArea, workspace: workspace)
                : AdvancedRleBlobExtractor.ExtractFeatures(region, grayView, minimumAreaPixels: minArea, workspace: workspace);

            var primaryPts = ImmutableArray.CreateBuilder<PointD>(blobs.Count);
            var contours = ImmutableArray.CreateBuilder<ImmutableArray<PointD>>(blobs.Count * 2);

            int totalHoles = 0;
            for (int b = 0; b < blobs.Count; b++)
            {
                AdvancedBlobFeature blob = blobs[b];
                totalHoles += blob.HoleCount;
                primaryPts.Add(new PointD(blob.Centroid.X + offsetX, blob.Centroid.Y + offsetY));

                // 1) Convex Hull contour
                ReadOnlySpan<Vector2> hullSpan = blob.ConvexHullVertices.Span;
                if (hullSpan.Length >= 3)
                {
                    var hullPoly = ImmutableArray.CreateBuilder<PointD>(hullSpan.Length);
                    for (int h = 0; h < hullSpan.Length; h++)
                    {
                        hullPoly.Add(new PointD(hullSpan[h].X + offsetX, hullSpan[h].Y + offsetY));
                    }
                    contours.Add(hullPoly.MoveToImmutable());
                }

                // 2) Rotating Calipers Minimum-Area Oriented Bounding Box contour
                (double sinB, double cosB) = Math.SinCos(blob.MinAreaBoxAngleRad);
                double hw = blob.MinAreaBoxWidth * 0.5;
                double hh = blob.MinAreaBoxHeight * 0.5;
                double cx = blob.MinAreaBoxCenter.X + offsetX;
                double cy = blob.MinAreaBoxCenter.Y + offsetY;
                contours.Add([
                    new PointD(cx + (-hw * cosB) - (-hh * sinB), cy + (-hw * sinB) + (-hh * cosB)),
                    new PointD(cx + (hw * cosB) - (-hh * sinB), cy + (hw * sinB) + (-hh * cosB)),
                    new PointD(cx + (hw * cosB) - (hh * sinB), cy + (hw * sinB) + (hh * cosB)),
                    new PointD(cx + (-hw * cosB) - (hh * sinB), cy + (-hw * sinB) + (hh * cosB))
                ]);
            }

            double elapsedUs = (Stopwatch.GetTimestamp() - startTick) * 1_000_000.0 / Stopwatch.Frequency;
            bool isPass = blobs.Count > 0;
            string badge = isPass
                ? $"블롭={blobs.Count}개 · 홀={totalHoles}개 · Solidity={blobs[0].Solidity:F2} · μ={blobs[0].MeanGray:F0}GV"
                : "고급 블롭 미검출";
            string detail = isPass
                ? (useWatershed
                    ? $"Meijster EDT 워터셰드 분리 완료: 분리 객체={blobs.Count}개 (개별 면적={string.Join("/", blobs.Select(x => $"{x.AreaPixels}px"))}, 원형도={blobs[0].Circularity:F2}, 평균명암={blobs[0].MeanGray:F1}±{blobs[0].StdDevGray:F1}GV)"
                    : $"RLE 고급 형상 분석: 객체={blobs.Count}개, 면적={blobs[0].AreaPixels}px, 내부홀={blobs[0].HoleCount}개(홀면적={blobs[0].HoleAreaPixels}px), 볼록도(Solidity)={blobs[0].Solidity:F3}, MinAreaBox={blobs[0].MinAreaBoxWidth:F1}×{blobs[0].MinAreaBoxHeight:F1}px({blobs[0].MinAreaBoxAngleRad * 180.0 / Math.PI:F1}°), 명암={blobs[0].MeanGray:F1}±{blobs[0].StdDevGray:F1}GV [{blobs[0].MinGray}..{blobs[0].MaxGray}]")
                : "지정된 임계값 이상의 유효 블롭을 찾지 못했습니다.";

            results.Add(new InspectionSuiteItemResult(
                target.RoiId,
                target.Name,
                isPass,
                badge,
                detail,
                target.Center,
                target.AngleRadians,
                target.Outline,
                contours.ToImmutable(),
                primaryPts.ToImmutable(),
                [],
                [],
                elapsedUs));
        }

        return results.MoveToImmutable();
    }

    private static ImmutableArray<InspectionSuiteItemResult> RunDataMatrix2DReader(
        MatchingImageReader reader,
        int imageWidth,
        int imageHeight,
        IReadOnlyList<InspectionSuiteRoiInput> targets,
        InspectionSuiteParameters parameters,
        CancellationToken token)
    {
        var results = ImmutableArray.CreateBuilder<InspectionSuiteItemResult>(targets.Count);
        double minIsoContrast = Math.Clamp(parameters.DataMatrixMinIsoContrast, 0.10, 0.90);
        int maxEccErrors = Math.Clamp(parameters.DataMatrixMaxEccErrors, 0, 16);

        for (int i = 0; i < targets.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var target = targets[i];
            long startTick = Stopwatch.GetTimestamp();

            var (crop, _, offsetX, offsetY) = ReadLocalCrop(reader, imageWidth, imageHeight, target, marginPixels: 12, token);
            var view = new ReadOnlyImageView2D<byte>(crop.Pixels.Span, crop.Width, crop.Height, crop.Stride);

            DataMatrixDecodeResult decoded = DataMatrixEcc200Decoder.Decode(view);
            double elapsedUs = (Stopwatch.GetTimestamp() - startTick) * 1_000_000.0 / Stopwatch.Frequency;

            var contours = ImmutableArray.CreateBuilder<ImmutableArray<PointD>>();
            var primaryPts = ImmutableArray.CreateBuilder<PointD>();
            var secondaryPts = ImmutableArray.CreateBuilder<PointD>();

            PointD referencePoint = target.Center;
            double referenceAngle = target.AngleRadians;
            double worldCx = target.Center.X;
            double worldCy = target.Center.Y;
            double worldLeft = 0.0;
            double worldTop = 0.0;
            double worldRight = 0.0;
            double worldBottom = 0.0;
            double barW = 0.0;
            double barH = 0.0;
            double angleDeg = 0.0;

            if (decoded.IsDecoded)
            {
                worldCx = decoded.CenterWorld.X + offsetX;
                worldCy = decoded.CenterWorld.Y + offsetY;
                barW = decoded.WidthPixels;
                barH = decoded.HeightPixels;
                double hw = barW * 0.5;
                double hh = barH * 0.5;
                referencePoint = new PointD(worldCx, worldCy);
                referenceAngle = decoded.AngleRad;
                angleDeg = decoded.AngleRad * (180.0 / Math.PI);

                (double sinA, double cosA) = Math.SinCos(referenceAngle);
                double ux = hw * cosA;
                double uy = hw * sinA;
                double vx = -hh * sinA;
                double vy = hh * cosA;

                PointD tl = new(worldCx - ux - vx, worldCy - uy - vy);
                PointD tr = new(worldCx + ux - vx, worldCy + uy - vy);
                PointD br = new(worldCx + ux + vx, worldCy + uy + vy);
                PointD bl = new(worldCx - ux + vx, worldCy - uy + vy);

                worldLeft = Math.Min(Math.Min(tl.X, tr.X), Math.Min(br.X, bl.X));
                worldRight = Math.Max(Math.Max(tl.X, tr.X), Math.Max(br.X, bl.X));
                worldTop = Math.Min(Math.Min(tl.Y, tr.Y), Math.Min(br.Y, bl.Y));
                worldBottom = Math.Max(Math.Max(tl.Y, tr.Y), Math.Max(br.Y, bl.Y));

                double arrowLen = Math.Max(14.0, hw * 0.85);
                double wingBack = Math.Max(5.0, arrowLen * 0.25);
                double wingSide = Math.Max(4.0, arrowLen * 0.18);
                PointD arrowTip = new(worldCx + (arrowLen * cosA), worldCy + (arrowLen * sinA));
                PointD wing1 = new(arrowTip.X - (wingBack * cosA) + (wingSide * sinA), arrowTip.Y - (wingBack * sinA) - (wingSide * cosA));
                PointD wing2 = new(arrowTip.X - (wingBack * cosA) - (wingSide * sinA), arrowTip.Y - (wingBack * sinA) + (wingSide * cosA));

                primaryPts.Add(referencePoint);
                secondaryPts.Add(tl);
                secondaryPts.Add(tr);
                secondaryPts.Add(br);
                secondaryPts.Add(bl);
                contours.Add([tl, tr, br, bl]);
                contours.Add([referencePoint, arrowTip, wing1, arrowTip, wing2]);
            }

            bool isPass = decoded.IsDecoded
                && decoded.SymbolContrast >= minIsoContrast
                && decoded.CorrectedByteErrors <= maxEccErrors;
            string badge = decoded.IsDecoded
                ? $"'{decoded.DecodedText}' @ ({worldCx:F1}, {worldCy:F1}) · θ={angleDeg:F2}° [{decoded.SymbolRows}×{decoded.SymbolCols} · {barW:F0}×{barH:F0}px] · ISO={decoded.OverallIsoGrade}"
                : "DataMatrix 판독 실패";
            string detail = decoded.IsDecoded
                ? $"2D 바코드 판독='{decoded.DecodedText}' ({decoded.SymbolRows}×{decoded.SymbolCols} 모듈), 바코드 중심좌표=({worldCx:F1}, {worldCy:F1}), 회전각={angleDeg:F2}°, 바운딩박스=[좌:{worldLeft:F1}, 상:{worldTop:F1} ~ 우:{worldRight:F1}, 하:{worldBottom:F1}] (크기={barW:F1}×{barH:F1}px, 각도={angleDeg:F2}°), 합격={isPass}, ISO/IEC 15415 등급={decoded.OverallIsoGrade} (대비={decoded.SymbolContrast:P0}≥{minIsoContrast:P0}, 변조={decoded.ModulationScore:P0}, 고정패턴={decoded.FixedPatternDamageScore:P0}, 비균일도={decoded.AxialNonUniformity:F3}, Reed-Solomon 복원 오류={decoded.CorrectedByteErrors}≤{maxEccErrors}바이트)"
                : "유효한 DataMatrix ECC 200 L-Finder 패턴 또는 Reed-Solomon 체크섬을 복원하지 못했습니다.";

            results.Add(new InspectionSuiteItemResult(
                target.RoiId,
                target.Name,
                isPass,
                badge,
                detail,
                referencePoint,
                referenceAngle,
                target.Outline,
                contours.ToImmutable(),
                primaryPts.ToImmutable(),
                secondaryPts.ToImmutable(),
                [],
                elapsedUs));
        }

        return results.MoveToImmutable();
    }

    private static ImmutableArray<InspectionSuiteItemResult> RunPreprocessingFftAndGabor(
        MatchingImageReader reader,
        int imageWidth,
        int imageHeight,
        IReadOnlyList<InspectionSuiteRoiInput> targets,
        InspectionSuiteParameters parameters,
        InspectionWorkspace workspace,
        CancellationToken token)
    {
        var results = ImmutableArray.CreateBuilder<InspectionSuiteItemResult>(targets.Count);
        float sigma = Math.Clamp(parameters.FftNotchOrGaborSigma, 1.0f, 24.0f);
        double peakRatio = Math.Clamp(parameters.FftPeakThresholdRatio, 2.0, 10.0);
        double gammaHigh = Math.Clamp(parameters.HomomorphicGammaHigh, 1.10, 3.00);

        for (int i = 0; i < targets.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var target = targets[i];
            long startTick = Stopwatch.GetTimestamp();

            var (crop, _, offsetX, offsetY) = ReadLocalCrop(reader, imageWidth, imageHeight, target, marginPixels: 0, token);
            int totalPixels = checked(crop.Width * crop.Height);
            byte[] dstBuf = new byte[totalPixels];

            bool isHomomorphic = target.Name.Contains("호모모픽", StringComparison.OrdinalIgnoreCase)
                || target.Name.Contains("Homomorphic", StringComparison.OrdinalIgnoreCase);
            bool isGabor = target.Name.Contains("가보", StringComparison.OrdinalIgnoreCase)
                || target.Name.Contains("Gabor", StringComparison.OrdinalIgnoreCase)
                || target.Name.Contains("텍스처", StringComparison.OrdinalIgnoreCase);

            int attenuatedPeaks = 0;
            string modeTag;
            if (isHomomorphic)
            {
                FrequencyAndTextureFilterEngine.ApplyHomomorphicFilter(
                    crop.Pixels.Span,
                    crop.Width,
                    crop.Height,
                    crop.Stride,
                    dstBuf,
                    gammaLow: 0.35,
                    gammaHigh: gammaHigh,
                    sigma: Math.Max(6.0, sigma * 8.0));
                modeTag = $"호모모픽 ln(I+1) 조명·반사율 분리 (γH={gammaHigh:F2})";
            }
            else if (isGabor)
            {
                FrequencyAndTextureFilterEngine.ApplyGaborTextureFilter(
                    crop.Pixels.Span,
                    crop.Width,
                    crop.Height,
                    crop.Stride,
                    dstBuf,
                    wavelength: Math.Max(4.0, sigma * 5.0),
                    orientationDeg: target.AngleRadians * (180.0 / Math.PI),
                    sigma: Math.Max(2.0, sigma * 2.2),
                    aspectRatio: 0.55);
                modeTag = $"2D 직교 위상 Gabor 텍스처 강조 (σ={sigma:F1})";
            }
            else
            {
                attenuatedPeaks = FrequencyAndTextureFilterEngine.ApplyFftPeriodicNotchFilter(
                    crop.Pixels.Span,
                    crop.Width,
                    crop.Height,
                    crop.Stride,
                    dstBuf,
                    minFrequencyRadius: 5,
                    notchSigma: Math.Max(2.5, sigma * 2.5),
                    peakThresholdRatio: peakRatio);
                modeTag = $"2D Radix-2 FFT 주기적 노치 (감쇠 피크={attenuatedPeaks}쌍, 감도={peakRatio:F1})";
            }

            var beforeStats = FrequencyAndTextureFilterEngine.ComputeRoiHistogramAndProfile(crop.Pixels.Span, crop.Width, crop.Height, crop.Stride);
            var afterStats = FrequencyAndTextureFilterEngine.ComputeRoiHistogramAndProfile(dstBuf, crop.Width, crop.Height, crop.Width);

            var outView = new ReadOnlyImageView2D<byte>(dstBuf, crop.Width, crop.Height, crop.Width);
            RleRegion postRegion = RleRegion.Threshold(outView, minIntensity: (byte)Math.Clamp(afterStats.OtsuThreshold + 12, 80, 240), maxIntensity: 255);
            List<BlobComponentFeature> postBlobs = RleBlobAnalyzer.AnalyzeComponents(postRegion, minimumAreaPixels: Math.Max(4, parameters.BlobMinimumAreaPixels), workspace);

            var primaryPts = ImmutableArray.CreateBuilder<PointD>(postBlobs.Count + 1);
            var contours = ImmutableArray.CreateBuilder<ImmutableArray<PointD>>(postBlobs.Count);
            for (int b = 0; b < postBlobs.Count; b++)
            {
                var blob = postBlobs[b];
                primaryPts.Add(new PointD(blob.Centroid.X + offsetX, blob.Centroid.Y + offsetY));
                double l = blob.MinCol + offsetX;
                double t = blob.MinRow + offsetY;
                double r = l + blob.BoundingWidth;
                double btm = t + blob.BoundingHeight;
                contours.Add([new PointD(l, t), new PointD(r, t), new PointD(r, btm), new PointD(l, btm)]);
            }

            if (primaryPts.Count == 0)
            {
                primaryPts.Add(target.Center);
            }

            double elapsedUs = (Stopwatch.GetTimestamp() - startTick) * 1_000_000.0 / Stopwatch.Frequency;
            bool isPass = afterStats.StdDevIntensity > 1.0;
            string badge = $"{modeTag} · Otsu={afterStats.OtsuThreshold}GV · 객체={postBlobs.Count}개";
            string detail = $"{modeTag} 완료 · 명암 μ={beforeStats.MeanIntensity:F1}→{afterStats.MeanIntensity:F1}GV, σ={beforeStats.StdDevIntensity:F1}→{afterStats.StdDevIntensity:F1}GV, Otsu={afterStats.OtsuThreshold}GV, 최대기울기={Math.Max(afterStats.HorizontalPeakGradient, afterStats.VerticalPeakGradient):F1} · 검출 객체={postBlobs.Count}개";

            results.Add(new InspectionSuiteItemResult(
                target.RoiId,
                target.Name,
                isPass,
                badge,
                detail,
                target.Center,
                target.AngleRadians,
                target.Outline,
                contours.ToImmutable(),
                primaryPts.ToImmutable(),
                [],
                [],
                elapsedUs,
                BuildGeometricGuidesForRoi(target, InspectionSuiteAlgorithmKind.PreprocessingFftAndGabor, parameters)));
        }

        return results.MoveToImmutable();
    }

    private static ImmutableArray<InspectionSuiteItemResult> RunBarcode1DReader(
        MatchingImageReader reader,
        int imageWidth,
        int imageHeight,
        IReadOnlyList<InspectionSuiteRoiInput> targets,
        InspectionSuiteParameters parameters,
        CancellationToken token)
    {
        var results = ImmutableArray.CreateBuilder<InspectionSuiteItemResult>(targets.Count);
        int scanlineCount = Math.Clamp(parameters.BarcodeScanlineCount, 3, 25);
        double minSymbolContrast = Math.Clamp(parameters.BarcodeMinSymbolContrast, 0.10, 0.85);

        for (int i = 0; i < targets.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var target = targets[i];
            long startTick = Stopwatch.GetTimestamp();

            var (crop, _, offsetX, offsetY) = ReadLocalCrop(reader, imageWidth, imageHeight, target, marginPixels: 12, token);
            Barcode1DDecodeResult decoded = Barcode1DDecoder.DecodeMultiScanline(
                crop.Pixels.Span,
                crop.Width,
                crop.Height,
                crop.Stride,
                scanlineCount: scanlineCount);

            double elapsedUs = (Stopwatch.GetTimestamp() - startTick) * 1_000_000.0 / Stopwatch.Frequency;
            var contours = ImmutableArray.CreateBuilder<ImmutableArray<PointD>>();
            var primaryPts = ImmutableArray.CreateBuilder<PointD>();
            var secondaryPts = ImmutableArray.CreateBuilder<PointD>();

            PointD referencePoint = target.Center;
            double referenceAngle = target.AngleRadians;
            double worldCx = target.Center.X;
            double worldCy = target.Center.Y;
            double worldLeft = 0.0;
            double worldTop = 0.0;
            double worldRight = 0.0;
            double worldBottom = 0.0;
            double barW = 0.0;
            double barH = 0.0;
            double angleDeg = 0.0;

            if (decoded.IsDecoded)
            {
                worldCx = decoded.CenterX + offsetX;
                worldCy = decoded.CenterY + offsetY;
                barW = decoded.WidthPixels;
                barH = decoded.HeightPixels;
                double hw = barW * 0.5;
                double hh = barH * 0.5;
                referencePoint = new PointD(worldCx, worldCy);
                referenceAngle = decoded.AngleRad;
                angleDeg = decoded.AngleDeg;

                (double sinA, double cosA) = Math.SinCos(referenceAngle);
                double ux = hw * cosA;
                double uy = hw * sinA;
                double vx = -hh * sinA;
                double vy = hh * cosA;

                PointD tl = new(worldCx - ux - vx, worldCy - uy - vy);
                PointD tr = new(worldCx + ux - vx, worldCy + uy - vy);
                PointD br = new(worldCx + ux + vx, worldCy + uy + vy);
                PointD bl = new(worldCx - ux + vx, worldCy - uy + vy);

                worldLeft = Math.Min(Math.Min(tl.X, tr.X), Math.Min(br.X, bl.X));
                worldRight = Math.Max(Math.Max(tl.X, tr.X), Math.Max(br.X, bl.X));
                worldTop = Math.Min(Math.Min(tl.Y, tr.Y), Math.Min(br.Y, bl.Y));
                worldBottom = Math.Max(Math.Max(tl.Y, tr.Y), Math.Max(br.Y, bl.Y));

                PointD scanStart = new(worldCx - ux, worldCy - uy);
                PointD scanEnd = new(worldCx + ux, worldCy + uy);
                double wingBack = Math.Max(6.0, hw * 0.14);
                double wingSide = Math.Max(4.5, hw * 0.10);
                PointD wing1 = new(scanEnd.X - (wingBack * cosA) + (wingSide * sinA), scanEnd.Y - (wingBack * sinA) - (wingSide * cosA));
                PointD wing2 = new(scanEnd.X - (wingBack * cosA) - (wingSide * sinA), scanEnd.Y - (wingBack * sinA) + (wingSide * cosA));

                primaryPts.Add(referencePoint);
                secondaryPts.Add(tl);
                secondaryPts.Add(tr);
                secondaryPts.Add(br);
                secondaryPts.Add(bl);
                contours.Add([tl, tr, br, bl]);
                contours.Add([scanStart, scanEnd, wing1, scanEnd, wing2]);
            }

            bool isPass = decoded.IsDecoded && decoded.IsChecksumValid && decoded.SymbolContrast >= minSymbolContrast;
            string badge = decoded.IsDecoded
                ? $"'{decoded.Payload}' @ ({worldCx:F1}, {worldCy:F1}) · θ={angleDeg:F2}° [{decoded.Symbology} · {barW:F0}×{barH:F0}px] · ISO={decoded.IsoGradeLetter}"
                : "1D 바코드 판독 실패";
            string detail = decoded.IsDecoded
                ? $"1D 바코드({decoded.Symbology}) 판독='{decoded.Payload}', 바코드 중심좌표=({worldCx:F1}, {worldCy:F1}), 회전각={angleDeg:F2}°, 바운딩박스=[좌:{worldLeft:F1}, 상:{worldTop:F1} ~ 우:{worldRight:F1}, 하:{worldBottom:F1}] (크기={barW:F1}×{barH:F1}px, 각도={angleDeg:F2}°), 합격={isPass}, 체크섬={decoded.ChecksumValue}({(decoded.IsChecksumValid ? "정상" : "오류")}), 유효스캔선={decoded.DecodedScanlineCount}/{scanlineCount}, ISO/IEC 15416 등급={decoded.IsoGradeLetter} (대비 SC={decoded.SymbolContrast:P0}≥{minSymbolContrast:P0}, 변조 MOD={decoded.Modulation:P0}, 디코더빌리티 DEC={decoded.Decodability:P0})"
                : "유효한 Code 128 / EAN-13 바코드 패턴 또는 체크섬을 복원하지 못했습니다.";

            results.Add(new InspectionSuiteItemResult(
                target.RoiId,
                target.Name,
                isPass,
                badge,
                detail,
                referencePoint,
                referenceAngle,
                target.Outline,
                contours.ToImmutable(),
                primaryPts.ToImmutable(),
                secondaryPts.ToImmutable(),
                [],
                elapsedUs));
        }

        return results.MoveToImmutable();
    }

    private static ImmutableArray<InspectionSuiteItemResult> RunPhotometricStereo(
        MatchingImageReader reader,
        int imageWidth,
        int imageHeight,
        IReadOnlyList<InspectionSuiteRoiInput> targets,
        InspectionSuiteParameters parameters,
        CancellationToken token)
    {
        var results = ImmutableArray.CreateBuilder<InspectionSuiteItemResult>(targets.Count);
        double slantDeg = Math.Clamp(parameters.PhotometricSlantAngleDeg, 15.0, 75.0);
        double curvThreshold = Math.Clamp(parameters.PhotometricCurvatureThreshold, 0.02, 0.35);
        int maxDefectPixels = Math.Clamp(parameters.PhotometricMaxDefectPixels, 1, 500);

        for (int i = 0; i < targets.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var target = targets[i];
            long startTick = Stopwatch.GetTimestamp();

            var (crop, _, offsetX, offsetY) = ReadLocalCrop(reader, imageWidth, imageHeight, target, marginPixels: 0, token);
            PhotometricStereoResult ps = PhotometricStereoEngine.AnalyzeCompositeOrSingleFrame(
                crop.Pixels.Span,
                crop.Width,
                crop.Height,
                crop.Stride,
                slantAngleDeg: slantDeg,
                curvatureThreshold: curvThreshold);

            double elapsedUs = (Stopwatch.GetTimestamp() - startTick) * 1_000_000.0 / Stopwatch.Frequency;
            var defectPts = ImmutableArray.CreateBuilder<PointD>();
            var primaryPts = ImmutableArray.CreateBuilder<PointD>();

            if (ps.DefectPixelCount > 0)
            {
                PointD peakWorld = new(offsetX + ps.PeakDefectX, offsetY + ps.PeakDefectY);
                defectPts.Add(peakWorld);
                primaryPts.Add(peakWorld);
            }
            else
            {
                primaryPts.Add(target.Center);
            }

            bool isPass = ps.DefectPixelCount <= maxDefectPixels;
            string statusTag = isPass ? "3D표면정상(OK)" : "3D요철결함(NG)";
            string badge = $"{statusTag} · |H|max={ps.MaxAbsMeanCurvature:F3} · 결함={ps.DefectPixelCount}/{maxDefectPixels}px";
            string detail = $"4방향 포토메트릭 스테레오(Slant={slantDeg:F0}°, |H|임계={curvThreshold:F3}): {statusTag}, 평균 알베도={ps.MeanAlbedo:F1}GV, 최대 평균곡률 |H|={ps.MaxAbsMeanCurvature:F4}, 가우시안곡률 |K|={ps.MaxAbsGaussianCurvature:F4}, 3D 요철 결함 픽셀={ps.DefectPixelCount}개 (영역={ps.DefectRegionCount}개)";

            results.Add(new InspectionSuiteItemResult(
                target.RoiId,
                target.Name,
                isPass,
                badge,
                detail,
                target.Center,
                target.AngleRadians,
                target.Outline,
                [],
                primaryPts.ToImmutable(),
                [],
                defectPts.ToImmutable(),
                elapsedUs));
        }

        return results.MoveToImmutable();
    }

    private static ImmutableArray<InspectionSuiteItemResult> RunGeometricTolerancing(
        MatchingImageReader reader,
        int imageWidth,
        int imageHeight,
        IReadOnlyList<InspectionSuiteRoiInput> targets,
        InspectionSuiteParameters parameters,
        CancellationToken token)
    {
        var results = ImmutableArray.CreateBuilder<InspectionSuiteItemResult>(targets.Count);
        double minGrad = Math.Clamp(parameters.GdtGradientThreshold, 6.0, 120.0);
        double angleTol = Math.Clamp(parameters.GdtAngleToleranceDeg, 0.2, 15.0);
        double concLimit = Math.Clamp(parameters.GdtConcentricityLimitPx, 0.2, 10.0);
        double distTol = Math.Clamp(parameters.GdtDistanceTolerancePx, 0.5, 20.0);

        for (int i = 0; i < targets.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var target = targets[i];
            long startTick = Stopwatch.GetTimestamp();

            var (crop, _, offsetX, offsetY) = ReadLocalCrop(reader, imageWidth, imageHeight, target, marginPixels: 8, token);
            GeometricToleranceResult gdt = GeometricToleranceAnalyzer.AnalyzeGeometricTolerances(
                crop.Pixels.Span,
                crop.Width,
                crop.Height,
                crop.Stride,
                nominalAngleDeg: 90.0,
                angleToleranceDeg: angleTol,
                nominalCenterDistancePx: 0.0,
                distanceTolerancePx: distTol,
                concentricityLimitPx: concLimit,
                gradientThreshold: minGrad);

            double elapsedUs = (Stopwatch.GetTimestamp() - startTick) * 1_000_000.0 / Stopwatch.Frequency;
            var contours = ImmutableArray.CreateBuilder<ImmutableArray<PointD>>();
            var primaryPts = ImmutableArray.CreateBuilder<PointD>();
            var secondaryPts = ImmutableArray.CreateBuilder<PointD>();

            if (gdt.IsMeasured)
            {
                PointD cornerPt = new(offsetX + gdt.DatumIntersectionX, offsetY + gdt.DatumIntersectionY);
                PointD outerCenter = new(offsetX + gdt.OuterCircleCenterX, offsetY + gdt.OuterCircleCenterY);
                PointD innerCenter = new(offsetX + gdt.InnerCircleCenterX, offsetY + gdt.InnerCircleCenterY);

                primaryPts.Add(cornerPt);
                primaryPts.Add(outerCenter);
                secondaryPts.Add(innerCenter);

                contours.Add(BuildCirclePolygon(outerCenter, gdt.OuterCircleRadiusPx, 36));
                contours.Add(BuildCirclePolygon(innerCenter, gdt.InnerCircleRadiusPx, 28));
                contours.Add([cornerPt, outerCenter]);
            }

            bool isPass = gdt.IsMeasured && gdt.IsAllWithinTolerance;
            string badge = gdt.IsMeasured
                ? $"직교={gdt.LineToLineAngleDeg:F2}° · 동심도={gdt.CircleConcentricityOffsetPx:F3}px · 수직거리={gdt.BoreToDatumDistancePx:F2}px"
                : "GD&T 데이텀/보어 미검출";
            string detail = gdt.IsMeasured
                ? $"GD&T 기하공차 계측: 데이텀 직교각={gdt.LineToLineAngleDeg:F3}° (편차={gdt.AngleErrorDeg:F3}°), 내/외경 동심도 편차 Δc={gdt.CircleConcentricityOffsetPx:F3}px (외경 R={gdt.OuterCircleRadiusPx:F2}px, 내경 R={gdt.InnerCircleRadiusPx:F2}px), 보어-수평데이텀 수직거리={gdt.BoreToDatumDistancePx:F2}px, 수직데이텀 거리={gdt.LineToLinePerpendicularDistancePx:F2}px"
                : "유효한 직교 데이텀 에지 및 동심 원형 보어를 충분히 검출하지 못했습니다.";

            results.Add(new InspectionSuiteItemResult(
                target.RoiId,
                target.Name,
                isPass,
                badge,
                detail,
                target.Center,
                target.AngleRadians,
                target.Outline,
                contours.ToImmutable(),
                primaryPts.ToImmutable(),
                secondaryPts.ToImmutable(),
                [],
                elapsedUs));
        }

        return results.MoveToImmutable();
    }
}
