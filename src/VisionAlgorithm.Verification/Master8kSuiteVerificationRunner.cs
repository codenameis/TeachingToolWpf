using System.Collections.Immutable;
using TeachingToolWpf.Imaging.Matching;
using VisionAlgorithm.OpenSrc.CornerAlignment.SyntheticCornerTestGen;
using VisionAlgorithm.OpenSrc.Synthetic8kTestGen;
using VisionGeometry;

namespace VisionAlgorithm.Verification;

/// <summary>
/// <see cref="IMaster8kSuiteRunner"/>의 합성 8K 검증 구현체.
/// 앱 조립 루트(Composition Root) 및 테스트 호스트에서만 주입된다.
/// </summary>
public sealed class Master8kSuiteVerificationRunner : IMaster8kSuiteRunner
{
    private const int ExpectedMasterMarkerCount = 27;
    private const float MaxCaliperAndCircleErrorPixels = 0.08f;
    private const int MinDetectedGoldenDefectPixels = 20;
    private const float MaxRleCentroidErrorPixels = 0.25f;
    private const float MaxStegerRmsErrorPixels = 0.05f;
    private const float MaxPadimLocalizationErrorPixels = 16.0f;
    private const double MaxHomographyRmsErrorMm = 1e-3;
    private const float MaxLensRoundTripErrorPixels = 1e-3f;
    private const double MaxSingleCornerPositionErrorPixels = 0.02;
    private const double MaxDiagonalCenterErrorPixels = 0.015;
    private const double HalfFactor = 0.5;

    public Task<InspectionSuiteExecutionOutput> RunAsync(
        CancellationToken cancellationToken,
        int unifiedSheetWidth = UnifiedTestImageSuiteGenerator.Full8kWidth,
        int unifiedSheetHeight = UnifiedTestImageSuiteGenerator.Full8kHeight)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(unifiedSheetWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(unifiedSheetHeight);

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            InspectionSuite8kVerificationReport report = InspectionSuite8kTestRunner.RunAllVerifications();

            var markers = ImmutableArray.CreateBuilder<InspectionSuiteItemResult>(ExpectedMasterMarkerCount);
            markers.Add(CreateZoneMarker(
                "1. Metrology (1D Caliper / RANSAC+IRLS / Taubin)",
                new PointD(1400, 1400),
                420,
                320,
                report.CaliperWidthErrorPixels < MaxCaliperAndCircleErrorPixels && report.CircleRadiusErrorPixels < MaxCaliperAndCircleErrorPixels,
                $"CalErr={report.CaliperWidthErrorPixels:F4}px · CirErr={report.CircleRadiusErrorPixels:F4}px",
                $"1D 폭 오차 {report.CaliperWidthErrorPixels:F4}px, 직선 각도 오차 {report.LineAngleErrorDegrees:F4}°, 원 반지름 오차 {report.CircleRadiusErrorPixels:F4}px (중심 오차 {report.CircleCenterErrorPixels:F4}px)"));

            markers.Add(CreateZoneMarker(
                "2. Golden Variation (Welford Mean/Std + Sobel Tolerance)",
                new PointD(3400, 1400),
                420,
                320,
                report.GoldenNormalFalseDefectPixels == 0 && report.GoldenDefectiveDetectedPixels > MinDetectedGoldenDefectPixels,
                $"Normal={report.GoldenNormalFalseDefectPixels}px · Defect={report.GoldenDefectiveDetectedPixels}px",
                $"정상 다이 오검출 {report.GoldenNormalFalseDefectPixels}px, 불량 다이 검출 {report.GoldenDefectiveDetectedPixels}px"));

            markers.Add(CreateZoneMarker(
                "3. RLE Blob & Interval Morphology",
                new PointD(5256, 2256),
                512,
                512,
                report.RleCleanedBlobCount == 1 && report.RleBlobCentroidErrorPixels < MaxRleCentroidErrorPixels,
                $"Blobs={report.RleCleanedBlobCount} · CentroidErr={report.RleBlobCentroidErrorPixels:F4}px",
                $"오프닝 노이즈 제거 후 블롭 {report.RleCleanedBlobCount}개, 무게중심 오차 {report.RleBlobCentroidErrorPixels:F4}px"));

            markers.Add(CreateZoneMarker(
                "4. Curvilinear Steger Hessian Ridge & Bead Continuity",
                new PointD(2600, 4200),
                1200,
                240,
                report.StegerCenterlineRmsErrorPixels < MaxStegerRmsErrorPixels && report.ContinuousBeadPassed && report.GappedBeadDetectedGapCount > 0,
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
                report.PadimAcceptedNormalDie && report.PadimDetectedDefectiveDie && report.PadimLocalizationErrorPixels <= MaxPadimLocalizationErrorPixels,
                $"NormalOK={report.PadimAcceptedNormalDie} · LocErr={report.PadimLocalizationErrorPixels:F2}px",
                $"정상 다이 통과={report.PadimAcceptedNormalDie}, 불량 다이 이상 탐지={report.PadimDetectedDefectiveDie}, 피크 위치 오차={report.PadimLocalizationErrorPixels:F2}px"));

            markers.Add(CreateZoneMarker(
                "7. 9-Point Hartley Homography & Brown-Conrady Lens",
                new PointD(4096, 4096),
                640,
                640,
                report.HomographyRmsErrorMm < MaxHomographyRmsErrorMm && report.LensUndistortRoundTripErrorPixels < MaxLensRoundTripErrorPixels,
                $"HomographyRMS={report.HomographyRmsErrorMm:E2}mm · LensErr={report.LensUndistortRoundTripErrorPixels:E2}px",
                $"9-Point 호모그래피 재투영 RMS={report.HomographyRmsErrorMm:E3}mm, Brown-Conrady 왜곡 역보정 왕복 오차={report.LensUndistortRoundTripErrorPixels:E3}px"));

            var cornerReport = CornerAlignment8kTestSuite.RunAllVerifications();
            markers.Add(CreateZoneMarker(
                "8. 2-Rake Virtual Corner & 4-Corner Diagonal / UVW Alignment",
                new PointD(2000, 6200),
                480,
                360,
                cornerReport.MaxPositionErrorPx <= MaxSingleCornerPositionErrorPixels && cornerReport.DiagonalCenterErrorPx <= MaxDiagonalCenterErrorPixels,
                $"CornerErr={cornerReport.MaxPositionErrorPx:F4}px · Diag8K={cornerReport.DiagonalCenterErrorPx:F4}px",
                $"20자세 가상 코너 최대 오차={cornerReport.MaxPositionErrorPx:F4}px, 8K 대각 정합 오차={cornerReport.DiagonalCenterErrorPx:F4}px, UVW 왕복={cornerReport.UvwRoundTripErrorMm:E2}mm"));

            var unifiedReport = UnifiedNewSubsystems8kRunner.RunAllVerifications(
                exportDirectory: null,
                sheetWidth: unifiedSheetWidth,
                sheetHeight: unifiedSheetHeight);
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

            var builtItems = markers.ToImmutable();
            int passCount = builtItems.Count(item => item.IsPass);
            double totalMs = report.TotalAlgorithmExecutionMilliseconds + unifiedReport.TotalElapsedMilliseconds;
            string summaryTitle = $"8K 전수 통합 검증 ({builtItems.Length}건: 7종 스위트 + 코너정합 + 티칭OCV·자동티칭 19종) · PASS {passCount}/{builtItems.Length} · {totalMs:F1}ms";
            string detailedReport = string.Join(Environment.NewLine, builtItems.Select(item => $"• [{(item.IsPass ? "PASS" : "FAIL")}] {item.Name}: {item.DetailLine}"));

            return new InspectionSuiteExecutionOutput(
                InspectionSuiteAlgorithmKind.Metrology,
                summaryTitle,
                detailedReport,
                builtItems,
                totalMs);
        }, cancellationToken);
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
        double hw = width * HalfFactor;
        double hh = height * HalfFactor;
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
}
