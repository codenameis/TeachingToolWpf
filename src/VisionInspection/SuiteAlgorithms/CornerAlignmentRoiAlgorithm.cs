using System.Collections.Immutable;
using System.Numerics;
using VisionAlgorithm.OpenSrc.Common;
using VisionAlgorithm.OpenSrc.CornerAlignment.Domain;
using VisionAlgorithm.OpenSrc.CornerAlignment.EdgeRake;
using VisionAlgorithm.OpenSrc.CornerAlignment.LineAndCorner;
using VisionGeometry;

namespace TeachingToolWpf.Imaging.Matching;

/// <summary>
/// 8번 2-Rake 서브픽셀 가상 코너 · 대각 정합 알고리즘.
/// </summary>
public sealed class CornerAlignmentRoiAlgorithm : RoiAlgorithmBase
{
    private const int CropMarginPixels = 36;
    private const int DefaultTukeyIterations = 5;
    private const float DefaultMinimumInlierRatio = 0.45f;
    private const float MinHalfSpanPx = 36.0f;
    private const double HalfSpanRatio = 0.46;
    private const float MinAvailableLegLengthPx = 48.0f;
    private const float MaxAvailableLegLengthRatio = 0.90f;
    private const float MinApexMarginLegRatio = 0.36f;
    private const float RakeSearchLengthPx = 28.0f;
    private const float NominalRightAngleDeg = 90.0f;
    private const float RadiansToDegrees = 180.0f / MathF.PI;
    private const float CornerExtensionLengthPx = 8.0f;
    private const float ProbeFractionNear = 0.35f;
    private const float ProbeFractionMid = 0.58f;
    private const float ProbeFractionFar = 0.80f;
    private const float ProbeSearchMultiplier = 1.85f;
    private const int MinProbeHitsForEstimate = 2;
    private const float ChamferProbeNearRatio = 0.28f;
    private const float ChamferProbeMidRatio = 0.52f;
    private const float MaxGuideApexOffsetRatio = 0.70f;
    private const float HalfScanFactor = 0.5f;
    private const double MinGuideArmLengthPx = 36.0;
    private const double GuideArmRatio = 0.44;
    private const double MaxGuideMarginRatio = 0.65;
    private const double GuideTickHalfLengthPx = 14.0;
    private const double SingleToothMidpoint = 0.5;

    private readonly record struct ValidatedCornerState(
        CornerAlignmentOptions Options,
        CornerDetectionOptions DetectionOptions);

    public override InspectionSuiteAlgorithmKind Kind => InspectionSuiteAlgorithmKind.CornerAlignment;

    public override InspectionSuiteCategory Category => InspectionSuiteCategory.MetrologyAndGeometry;

    public override string ShortName => "8. 2-Rake 서브픽셀 가상 코너 · 대각 정합";

    public override string ShortPillLabel => "2-Rake 가상 코너";

    public override string AssignmentBadge => "스위트 · 코너정합";

    public override string Description =>
        "모따기·라운딩·칩핑·버를 회피하는 2-Rake 다중 1D 서브픽셀 캘리퍼와 RANSAC + Tukey Biweight 직선 교차로 가상 코너 꼭짓점 및 4-코너 대각 정합을 수행합니다.";

    public override string UsageGuide =>
        "① 기판이나 부품의 코너(모따기·라운딩 포함) 부근에 ROI 중심을 배치하세요 (샘플 열기 시 4개 모따기 코너 ROI 자동 배치).\n② 코너 꼭짓점 여유 구간을 건너뛰는 2-Rake 서브픽셀 에지 스캔으로 두 직선의 정밀 가상 교차점(청록 점)과 내각(°)을 즉시 산출합니다.";

    public override string SampleSceneSlug => "corner";

    public override string SampleSceneTitle => "8. 2-Rake 서브픽셀 가상 코너·4코너 대각 정합 샘플 (1200×800)";

    public override InspectionAlgorithmHelpSpec HelpSpec { get; } = new(
        "모따기·라운딩 코너 정합 (2-Rake RANSAC + Tukey Biweight 가상 꼭짓점)",
        "코너 꼭짓점 마진 배제(Apex Ignore) · 직교 2-Rake 서브픽셀 에지 · Tukey IRLS 직선 교차점",
        "• 수식 원리: 실제 기판/칩 코너는 모따기(Chamfer), 라운딩(Fillet), 깨짐(Chipping), 버(Burr)로 인해 코너 끝점을 직접 찾으면 큰 오차가 발생합니다.\n" +
        "• 꼭짓점 반경(Apex Ignore Margin)을 건너뛰고 직교하는 두 변의 깨끗한 직선 구간에 다중 1D 서브픽셀 캘리퍼 레이크를 투사한 뒤, RANSAC + Tukey Biweight IRLS로 두 직선 L_1, L_2를 피팅하여 대수적 교차점 P_apex = L_1 × L_2와 사잇각(90°±Δθ)을 구합니다.",
        "① 부품의 모서리(코너)가 ROI 중앙 근처에 오도록 ROI를 배치합니다(4개 코너에 각각 ROI를 배치하면 대각선 길이·중심 정렬까지 자동 계산).\n" +
        "② 모따기 크기에 맞춰 [꼭짓점 배제 여유(px)]를 조절하면 두 직선(초록)과 연장된 가상 코너 교차점(청록 점)이 실시간 표시됩니다.",
        "• 꼭짓점 배제 여유 (8~120 px): 모따기나 라운딩이 끝나는 지점보다 약간 더 크게 설정해야 곡선 구간이 직선 피팅에 섞이지 않습니다.\n" +
        "• RANSAC 인라이어 임계치 (0.1~3.0 px): 에지 거칠기에 맞춰 0.4~0.8 px로 설정하세요.\n" +
        "• 직교 허용 공차 (0.5~25.0°): 두 변의 사잇각이 90°에서 이 값 이상 벗어나면 NG로 판정합니다.",
        "• 한쪽 변이 안 잡힐 때: ROI 크기를 키워 꼭짓점 배제 구간 바깥으로 충분한 직선 변 길이가 확보되게 하세요.");

    public override string FormatParameterSummary(InspectionSuiteParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        CornerAlignmentOptions opt = parameters.CornerAlignment;
        return $"σ={opt.GaussianSigma:F2} · 대비≥{opt.MinimumContrast:F1} · 레이크={opt.CaliperRakeCount}개 · 모따기 회피={opt.ApexIgnoreMargin:F0}px · 직각허용=±{opt.OrthogonalityToleranceDeg:F1}° · RANSAC={opt.RansacThresholdPixels:F2}px";
    }

    public override ImmutableArray<ImmutableArray<PointD>> BuildGeometricGuides(
        InspectionSuiteRoiInput target,
        InspectionSuiteParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(parameters);

        CornerAlignmentOptions opt = parameters.CornerAlignment;
        double armX = Math.Max(MinGuideArmLengthPx, target.Width * GuideArmRatio);
        double armY = Math.Max(MinGuideArmLengthPx, target.Height * GuideArmRatio);
        double margin = Math.Clamp(opt.ApexIgnoreMargin, CornerAlignmentOptions.MinApexIgnoreMargin, Math.Min(armX, armY) * MaxGuideMarginRatio);
        int teeth = Math.Clamp(opt.CaliperRakeCount, CornerAlignmentOptions.MinGuideTeethCount, CornerAlignmentOptions.MaxGuideTeethCount);

        var guides = ImmutableArray.CreateBuilder<ImmutableArray<PointD>>(2 + (teeth * 2));
        guides.Add([LocalToWorld(target, margin, 0), LocalToWorld(target, armX, 0)]);
        guides.Add([LocalToWorld(target, 0, margin), LocalToWorld(target, 0, armY)]);

        for (int t = 0; t < teeth; t++)
        {
            double u = teeth == 1 ? SingleToothMidpoint : (double)t / (teeth - 1);
            double xPos = margin + (u * (armX - margin));
            double yPos = margin + (u * (armY - margin));
            guides.Add([LocalToWorld(target, xPos, -GuideTickHalfLengthPx), LocalToWorld(target, xPos, GuideTickHalfLengthPx)]);
            guides.Add([LocalToWorld(target, -GuideTickHalfLengthPx, yPos), LocalToWorld(target, GuideTickHalfLengthPx, yPos)]);
        }

        return guides.MoveToImmutable();
    }

    public override ImmutableArray<InspectionSuiteItemResult> Execute(RoiInspectionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        CornerAlignmentOptions opt = context.Parameters.CornerAlignment.Validate();
        var detectionOptions = new CornerDetectionOptions(
            GaussianSigma: opt.GaussianSigma,
            MinimumContrast: opt.MinimumContrast,
            RansacInlierThresholdPixels: opt.RansacThresholdPixels,
            TukeyIterations: DefaultTukeyIterations,
            OrthogonalityToleranceDeg: opt.OrthogonalityToleranceDeg,
            MinimumInlierRatio: DefaultMinimumInlierRatio);

        var state = new ValidatedCornerState(opt, detectionOptions);
        return ExecuteEach(context, CropMarginPixels, state, EvaluateTarget);
    }

    private InspectionSuiteItemResult EvaluateTarget(
        RoiInspectionContext context,
        InspectionSuiteRoiInput roi,
        LocalRoiCrop localCrop,
        ValidatedCornerState state,
        Func<double> measureElapsedMicroseconds)
    {
        var (crop, localCenter, offsetX, offsetY) = localCrop;
        var view = new ReadOnlyImageView2D<byte>(crop.Pixels.Span, crop.Width, crop.Height, crop.Stride);

        float halfW = MathF.Max(MinHalfSpanPx, (float)(roi.Width * HalfSpanRatio));
        float halfH = MathF.Max(MinHalfSpanPx, (float)(roi.Height * HalfSpanRatio));
        float cos = MathF.Cos((float)roi.AngleRadians);
        float sin = MathF.Sin((float)roi.AngleRadians);
        var axisX = new Vector2(cos, sin);
        var axisY = new Vector2(-sin, cos);
        int caliperCount = state.Options.CaliperRakeCount;
        float projWidth = state.Options.CaliperProjectionWidth;

        ReadOnlySpan<(float Sx, float Sy)> quadrants =
        [
            (1.0f, 1.0f),
            (-1.0f, 1.0f),
            (-1.0f, -1.0f),
            (1.0f, -1.0f)
        ];

        VirtualCornerResult bestCorner = VirtualCornerResult.Invalid;
        RakeSearchRegion bestLeg1 = default;
        RakeSearchRegion bestLeg2 = default;
        SubpixelEdgePoint[] bestInliers = [];
        List<SubpixelEdgePoint> bestOutliers = [];

        for (int q = 0; q < quadrants.Length; q++)
        {
            (float sx, float sy) = quadrants[q];
            Vector2 legDir1 = axisX * sx;
            Vector2 legDir2 = axisY * sy;

            Vector2 approxApex = EstimateCornerApexInRoi(
                view,
                localCenter,
                legDir1,
                legDir2,
                halfW,
                halfH,
                projWidth,
                state.DetectionOptions);

            float relU = Vector2.Dot(approxApex - localCenter, legDir1);
            float relV = Vector2.Dot(approxApex - localCenter, legDir2);
            float availLen1 = Math.Clamp(halfW - relU, MinAvailableLegLengthPx, (float)roi.Width * MaxAvailableLegLengthRatio);
            float availLen2 = Math.Clamp(halfH - relV, MinAvailableLegLengthPx, (float)roi.Height * MaxAvailableLegLengthRatio);

            float userApexMargin = state.Options.ApexIgnoreMargin;
            float apexMargin1 = MathF.Max(userApexMargin, availLen1 * MinApexMarginLegRatio);
            float apexMargin2 = MathF.Max(userApexMargin, availLen2 * MinApexMarginLegRatio);

            Vector2 end1 = approxApex + (legDir1 * availLen1);
            Vector2 end2 = approxApex + (legDir2 * availLen2);

            var leg1 = new RakeSearchRegion(
                StartPoint: approxApex,
                EndPoint: end1,
                SearchLength: RakeSearchLengthPx,
                ProjectionWidth: projWidth,
                CaliperCount: caliperCount,
                ApexIgnoreMargin: apexMargin1,
                Polarity: CaliperPolarity.Any,
                ExplicitScanDirection: legDir2);

            var leg2 = new RakeSearchRegion(
                StartPoint: approxApex,
                EndPoint: end2,
                SearchLength: RakeSearchLengthPx,
                ProjectionWidth: projWidth,
                CaliperCount: caliperCount,
                ApexIgnoreMargin: apexMargin2,
                Polarity: CaliperPolarity.Any,
                ExplicitScanDirection: legDir1);

            VirtualCornerResult cand = VirtualCornerAligner.DetectVirtualCornerWithPoints(
                view,
                leg1,
                leg2,
                state.DetectionOptions,
                out SubpixelEdgePoint[] candInliers,
                out SubpixelEdgePoint[] candOutliers);

            if (cand.IsValid && (!bestCorner.IsValid || cand.InlierRatio > bestCorner.InlierRatio))
            {
                bestCorner = cand;
                bestLeg1 = leg1;
                bestLeg2 = leg2;
                bestInliers = candInliers;
                bestOutliers = new List<SubpixelEdgePoint>(candOutliers);

                CollectChamferRejectedPoints(view, approxApex, legDir1, legDir2, apexMargin1, apexMargin2, projWidth, state.DetectionOptions, bestOutliers);
            }
        }

        double us = measureElapsedMicroseconds();

        PointD worldVertex = bestCorner.IsValid
            ? new PointD(offsetX + bestCorner.CornerX, offsetY + bestCorner.CornerY)
            : roi.Center;
        bool isPass = bestCorner.IsValid;
        float angleDevDeg = bestCorner.IsValid ? MathF.Abs(NominalRightAngleDeg - bestCorner.IncludedAngleDeg) : 0.0f;
        float line1AngleDeg = bestCorner.IsValid ? bestCorner.Line1.AngleRad * RadiansToDegrees : 0.0f;
        float line2AngleDeg = bestCorner.IsValid ? bestCorner.Line2.AngleRad * RadiansToDegrees : 0.0f;

        string badge = bestCorner.IsValid
            ? $"Corner ({worldVertex.X:F2},{worldVertex.Y:F2}) · {bestCorner.IncludedAngleDeg:F3}°"
            : "Corner NG (직교 에지 미검출)";
        string detail = bestCorner.IsValid
            ? $"2-Rake 가상 코너=({worldVertex.X:F3}, {worldVertex.Y:F3}), Line1={line1AngleDeg:F3}°, Line2={line2AngleDeg:F3}°, 내각={bestCorner.IncludedAngleDeg:F3}° (직각편차={angleDevDeg:F3}°), 인라이어={bestInliers.Length}점 / 기각(모따기·결함)={bestOutliers.Count}점"
            : "유효한 직교 두 변 에지를 찾지 못했습니다 (내각 90°±5° 이탈 또는 인라이어 부족).";

        var builder = new InspectionItemResultBuilder(roi)
            .WithPose(worldVertex, bestCorner.IsValid ? bestCorner.OrientationRad : roi.AngleRadians);

        if (bestCorner.IsValid)
        {
            Vector2 cornerLocal = bestCorner.CornerPoint;

            Vector2 t1 = bestCorner.Line1.Tangent;
            if (Vector2.Dot(bestLeg1.EndPoint - cornerLocal, t1) < 0.0f)
            {
                t1 = -t1;
            }

            Vector2 far1 = bestCorner.Line1.ProjectPoint(bestLeg1.EndPoint);
            Vector2 extStart1 = cornerLocal - (t1 * CornerExtensionLengthPx);
            builder.AddContour([
                new PointD(offsetX + extStart1.X, offsetY + extStart1.Y),
                new PointD(offsetX + far1.X, offsetY + far1.Y)
            ]);

            Vector2 t2 = bestCorner.Line2.Tangent;
            if (Vector2.Dot(bestLeg2.EndPoint - cornerLocal, t2) < 0.0f)
            {
                t2 = -t2;
            }

            Vector2 far2 = bestCorner.Line2.ProjectPoint(bestLeg2.EndPoint);
            Vector2 extStart2 = cornerLocal - (t2 * CornerExtensionLengthPx);
            builder.AddContour([
                new PointD(offsetX + extStart2.X, offsetY + extStart2.Y),
                new PointD(offsetX + far2.X, offsetY + far2.Y)
            ]);

            builder.AddPrimaryPoint(worldVertex);
            for (int i = 0; i < bestInliers.Length; i++)
            {
                builder.AddPrimaryPoint(new PointD(offsetX + bestInliers[i].X, offsetY + bestInliers[i].Y));
            }

            for (int i = 0; i < bestOutliers.Count; i++)
            {
                builder.AddDefectPoint(new PointD(offsetX + bestOutliers[i].X, offsetY + bestOutliers[i].Y));
            }

            var rakeGuides = ImmutableArray.CreateBuilder<ImmutableArray<PointD>>();
            AppendRakeGuideContours(bestLeg1, offsetX, offsetY, rakeGuides);
            AppendRakeGuideContours(bestLeg2, offsetX, offsetY, rakeGuides);
            builder.WithGuideContours(rakeGuides.ToImmutable());
        }
        else
        {
            builder.WithGuideContours(BuildGeometricGuides(roi, context.Parameters));
        }

        return builder.Build(isPass, badge, detail, us);
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
        ReadOnlySpan<float> probeFractions = [ProbeFractionNear, ProbeFractionMid, ProbeFractionFar];
        float sumV = 0.0f;
        int countV = 0;
        for (int i = 0; i < probeFractions.Length; i++)
        {
            Vector2 probeCenter = localCenter + (legDir1 * (halfW * probeFractions[i]));
            if (Simd1DProjectionCaliper.TryMeasureEdge(
                view,
                probeCenter,
                legDir2,
                halfH * ProbeSearchMultiplier,
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

        float sumU = 0.0f;
        int countU = 0;
        for (int i = 0; i < probeFractions.Length; i++)
        {
            Vector2 probeCenter = localCenter + (legDir2 * (halfH * probeFractions[i]));
            if (Simd1DProjectionCaliper.TryMeasureEdge(
                view,
                probeCenter,
                legDir1,
                halfW * ProbeSearchMultiplier,
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

        float estU = countU >= MinProbeHitsForEstimate ? (sumU / countU) : 0.0f;
        float estV = countV >= MinProbeHitsForEstimate ? (sumV / countV) : 0.0f;
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
        ReadOnlySpan<float> chamferProbes = [ChamferProbeNearRatio, ChamferProbeMidRatio];
        for (int i = 0; i < chamferProbes.Length; i++)
        {
            float d1 = apexMargin1 * chamferProbes[i];
            Vector2 c1 = approxApex + (legDir1 * d1);
            if (!Simd1DProjectionCaliper.TryMeasureEdge(
                view, c1, legDir2, RakeSearchLengthPx, projWidth, CaliperPolarity.Any, options.GaussianSigma, options.MinimumContrast, out SubpixelEdgePoint hit1)
                && hit1.Magnitude >= options.MinimumContrast)
            {
                outliersOut.Add(hit1);
            }

            float d2 = apexMargin2 * chamferProbes[i];
            Vector2 c2 = approxApex + (legDir2 * d2);
            if (!Simd1DProjectionCaliper.TryMeasureEdge(
                view, c2, legDir1, RakeSearchLengthPx, projWidth, CaliperPolarity.Any, options.GaussianSigma, options.MinimumContrast, out SubpixelEdgePoint hit2)
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
        float startOffset = Math.Clamp(region.ApexIgnoreMargin, 0.0f, totalLen * MaxGuideApexOffsetRatio);
        float usableLen = MathF.Max(1.0f, totalLen - startOffset);
        Vector2 activeStart = region.StartPoint + (legDir * startOffset);
        Vector2 activeEnd = region.EndPoint;

        guides.Add([
            new PointD(offsetX + activeStart.X, offsetY + activeStart.Y),
            new PointD(offsetX + activeEnd.X, offsetY + activeEnd.Y)
        ]);

        int spokes = Math.Clamp(region.CaliperCount, CornerAlignmentOptions.MinGuideTeethCount, CornerAlignmentOptions.MaxGuideTeethCount);
        float halfScan = region.SearchLength * HalfScanFactor;
        for (int s = 0; s < spokes; s++)
        {
            float t = spokes == 1 ? HalfScanFactor : (float)s / (spokes - 1);
            Vector2 center = activeStart + (legDir * (t * usableLen));
            Vector2 p0 = center - (scanDir * halfScan);
            Vector2 p1 = center + (scanDir * halfScan);
            guides.Add([
                new PointD(offsetX + p0.X, offsetY + p0.Y),
                new PointD(offsetX + p1.X, offsetY + p1.Y)
            ]);
        }
    }
}
