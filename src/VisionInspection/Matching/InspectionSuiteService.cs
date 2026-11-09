using System.Collections.Immutable;
using System.Diagnostics;
using System.Numerics;
using VisionAlgorithm.OpenSrc.BlobRle;
using VisionAlgorithm.OpenSrc.Calibration;
using VisionAlgorithm.OpenSrc.Common;
using VisionAlgorithm.OpenSrc.Curvilinear;
using VisionAlgorithm.OpenSrc.EdgeFewShot.Engine;
using VisionAlgorithm.OpenSrc.GoldenVariation;
using VisionAlgorithm.OpenSrc.Metrology;
using VisionAlgorithm.OpenSrc.PatchAnomaly;
using VisionAlgorithm.OpenSrc.Synthetic8kTestGen;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Imaging.Matching;

/// <summary>
/// 상용 정밀 검사 스위트 7종 서브 알고리즘 구분.
/// </summary>
public enum InspectionSuiteAlgorithmKind
{
    Metrology = 0,
    GoldenVariation = 1,
    BlobRle = 2,
    Curvilinear = 3,
    FewShot = 4,
    PatchAnomaly = 5,
    Calibration = 6
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
    float CaliperProjectionWidth = 11.0f);

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
    ImmutableArray<ImmutableArray<PointD>> GuideContours = default);

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
/// <c>VisionAlgorithm.OpenSrc.InspectionSuite</c> 7종 알고리즘을 캔버스 ROI 및 8K 마스터 검증과 연결하는 서비스.
/// </summary>
public sealed class InspectionSuiteService(IImageLoader loader, Func<VideoFrame?> retainFrame) : IInspectionSuiteService
{
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

            using var reader = await MatchingImageReader.OpenAsync(image, live, loader, retainFrame, token).ConfigureAwait(false);
            reader.PreferDirectGray = true;
            using var workspace = new InspectionWorkspace();

            var sw = Stopwatch.StartNew();
            var items = algorithm switch
            {
                InspectionSuiteAlgorithmKind.Metrology => RunMetrology(reader, image.Width, image.Height, targetRois, parameters, workspace, token),
                InspectionSuiteAlgorithmKind.GoldenVariation => RunGoldenVariation(reader, image.Width, image.Height, targetRois, referenceRois, parameters, workspace, token),
                InspectionSuiteAlgorithmKind.BlobRle => RunBlobRle(reader, image.Width, image.Height, targetRois, parameters, workspace, token),
                InspectionSuiteAlgorithmKind.Curvilinear => RunCurvilinear(reader, image.Width, image.Height, targetRois, parameters, workspace, token),
                InspectionSuiteAlgorithmKind.FewShot => RunFewShot(reader, image.Width, image.Height, targetRois, referenceRois, workspace, token),
                InspectionSuiteAlgorithmKind.PatchAnomaly => RunPatchAnomaly(reader, image.Width, image.Height, targetRois, referenceRois, parameters, workspace, token),
                InspectionSuiteAlgorithmKind.Calibration => RunCalibration(targetRois, parameters, image.Width, image.Height),
                _ => throw new ArgumentOutOfRangeException(nameof(algorithm))
            };
            sw.Stop();

            int passCount = items.Count(item => item.IsPass);
            string algoName = GetAlgorithmShortName(algorithm);
            string summaryTitle = $"{algoName} 완료 · 합격 {passCount}/{items.Length}개 · {sw.Elapsed.TotalMilliseconds:F2} ms";
            string detailedReport = string.Join(Environment.NewLine, items.Select((item, idx) => $"[{idx + 1}] {item.Name}: {item.DetailLine} ({item.ElapsedMicroseconds:F1} µs)"));

            return new InspectionSuiteExecutionOutput(algorithm, summaryTitle, detailedReport, items, sw.Elapsed.TotalMilliseconds);
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

            var builtItems = markers.MoveToImmutable();
            int passCount = builtItems.Count(item => item.IsPass);
            string summaryTitle = $"8K(8192×8192) 7종 전수 검증 · PASS {passCount}/7 · 생성 {report.SceneGenerationMilliseconds:F1}ms / 알고리즘 {report.TotalAlgorithmExecutionMilliseconds:F1}ms";
            string detailedReport = string.Join(Environment.NewLine, builtItems.Select(item => $"• [{(item.IsPass ? "PASS" : "FAIL")}] {item.Name}: {item.DetailLine}"));

            return new InspectionSuiteExecutionOutput(
                InspectionSuiteAlgorithmKind.Metrology,
                summaryTitle,
                detailedReport,
                builtItems,
                report.TotalAlgorithmExecutionMilliseconds);
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
                minimumDefectAreaPixels: 6,
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

            List<BlobComponentFeature> blobs = RleBlobAnalyzer.AnalyzeComponents(region, minimumAreaPixels: 8, workspace);
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

        EmbeddingFewShotClassifier model = EmbeddingFewShotClassifier.Train(trainingExamples.ToArray(), maxSubClustersPerClass: 2);

        for (int i = 0; i < targets.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var target = targets[i];
            long startTick = Stopwatch.GetTimestamp();

            float[] queryEmb = ExtractCompactGeMEmbedding(reader, imageWidth, imageHeight, target, patchW, patchH, workspace, token);
            FewShotPrediction pred = model.Predict(queryEmb, workspace);

            double elapsedUs = (Stopwatch.GetTimestamp() - startTick) * 1_000_000.0 / Stopwatch.Frequency;
            bool isPass = !pred.IsOutOfDistribution && pred.PredictedClassLabel == 0;
            string classTag = pred.IsOutOfDistribution
                ? "OOD(미등록 이상치)"
                : (pred.PredictedClassLabel == 0 ? "Good(정상)" : $"Class {pred.PredictedClassLabel}(불량)");

            results.Add(new InspectionSuiteItemResult(
                target.RoiId,
                target.Name,
                isPass,
                $"{classTag} · D_M={pred.MahalanobisDistance:F2}",
                $"{classTag} · 마할라노비스 거리={pred.MahalanobisDistance:F3} (임계치={pred.RejectionThreshold:F2}) · 신뢰도={pred.ConfidenceScore * 100f:F1}%",
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
            float peakLocalX = res.PeakAnomalyLocalPosition.X - (roiWidth * 0.5f);
            float peakLocalY = res.PeakAnomalyLocalPosition.Y - (roiHeight * 0.5f);
            Vector2 peakWorldInCrop = localPose.LocalToWorld(peakLocalX, peakLocalY);
            PointD peakWorld = new(peakWorldInCrop.X + offsetX, peakWorldInCrop.Y + offsetY);

            double elapsedUs = (Stopwatch.GetTimestamp() - startTick) * 1_000_000.0 / Stopwatch.Frequency;
            bool isPass = !res.IsAnomalous;
            string statusTag = isPass ? "PaDiM 정상(OK)" : "PaDiM 이상(ANOMALY)";

            results.Add(new InspectionSuiteItemResult(
                target.RoiId,
                target.Name,
                isPass,
                $"{statusTag} · Max={res.ImageAnomalyScore:F2}",
                $"{statusTag} · 최대 패치 마할라노비스 점수={res.ImageAnomalyScore:F3} (임계치={detector.AnomalyThreshold:F3}, 초과픽셀={res.AnomalousPixelCount}) · 피크 좌표=({peakWorld.X:F1}, {peakWorld.Y:F1})",
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
        double halfDiag = Math.Sqrt((roi.Width * roi.Width) + (roi.Height * roi.Height)) * 0.55 + marginPixels;
        int radiusX = (int)Math.Ceiling(Math.Max(roi.Width * 0.5 + marginPixels, halfDiag));
        int radiusY = (int)Math.Ceiling(Math.Max(roi.Height * 0.5 + marginPixels, halfDiag));

        int left = Math.Clamp((int)Math.Floor(roi.Center.X) - radiusX, 0, Math.Max(0, imageWidth - 1));
        int top = Math.Clamp((int)Math.Floor(roi.Center.Y) - radiusY, 0, Math.Max(0, imageHeight - 1));
        int right = Math.Clamp((int)Math.Ceiling(roi.Center.X) + radiusX, left + 1, imageWidth);
        int bottom = Math.Clamp((int)Math.Ceiling(roi.Center.Y) + radiusY, top + 1, imageHeight);

        var crop = reader.Read(left, top, right - left, bottom - top, token);
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
        _ => kind.ToString()
    };
}
