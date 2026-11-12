using System.Collections.Immutable;
using VisionAlgorithm.OpenSrc.Common;
using VisionAlgorithm.OpenSrc.Metrology;
using VisionGeometry;

namespace TeachingToolWpf.Imaging.Matching;

/// <summary>
/// 14번 복합 스코어링 캘리퍼 및 Fitzgibbon + Sampson Gauss-Newton 타원 피팅 알고리즘.
/// </summary>
public sealed class AdvancedMetrologyEllipseRoiAlgorithm : RoiAlgorithmBase
{
    private const int CropMarginPixels = 28;
    private const float MinCaliperScanLengthPx = 24.0f;
    private const float CaliperScanWidthRatio = 0.90f;
    private const float MinDynamicProjectionHeightPx = 5.0f;
    private const float DynamicProjectionHeightRatio = 0.80f;
    private const float DefaultExpectedSpanOverrideEpsilonPx = 0.5f;
    private const float MinAutoExpectedSpanPx = 14.0f;
    private const float AutoExpectedSpanWidthRatio = 0.34f;
    private const float PositionToleranceScanRatio = 0.22f;
    private const float SpanToleranceRatio = 0.30f;
    private const float MinNominalSemiMajorPx = 12.0f;
    private const float NominalSemiMajorWidthRatio = 0.36f;
    private const float MinNominalSemiMinorPx = 10.0f;
    private const float NominalSemiMinorHeightRatio = 0.30f;
    private const float MinRadialScanLengthPx = 20.0f;
    private const float RadialScanAxisRatio = 0.72f;
    private const int EllipsePolygonSegments = 56;
    private const double RadiansToDegrees = 180.0 / Math.PI;
    private const double MinGuideHalfScanPx = 14.0;
    private const double GuideHalfScanWidthRatio = 0.45;
    private const double HalfFactor = 0.5;
    private const double MinGuideHalfProjPx = 3.0;
    private const double MinGuideMaxHalfProjPx = 4.0;
    private const double GuideMaxHalfProjHeightRatio = 0.42;
    private const double MinGuideNominalSemiMajorPx = 10.0;
    private const double GuideNominalSemiMajorWidthRatio = 0.38;
    private const double MinGuideNominalSemiMinorPx = 8.0;
    private const double GuideNominalSemiMinorHeightRatio = 0.32;
    private const double GuideSpokeInnerScale = 0.72;
    private const double GuideSpokeOuterScale = 1.24;

    private readonly record struct ValidatedAdvancedMetrologyState(
        AdvancedMetrologyOptions RawOptions,
        AdvancedMetrologyOptions ValidatedOptions,
        int ProjectionStripCount);

    public override InspectionSuiteAlgorithmKind Kind => InspectionSuiteAlgorithmKind.AdvancedMetrologyEllipse;

    public override InspectionSuiteCategory Category => InspectionSuiteCategory.MetrologyAndGeometry;

    public override string ShortName => "14. [본검사] 복합 스코어링 캘리퍼(First/Last/Span) · Fitzgibbon 타원 피팅";

    public override string ShortPillLabel => "복합캘리퍼·타원 피팅";

    public override string AssignmentBadge => "스위트 · 타원/복합캘리퍼";

    public override string Description =>
        "Cognex CogCaliperScoringFunction(First/Last/Expected Span 연속 가우시안 친화도) 복합 캘리퍼와 Fitzgibbon(1999) + Sampson Gauss-Newton 직교 거리 타원(Ellipse) 피팅을 수행합니다.";

    public override string UsageGuide =>
        "① 다중 간섭 에지가 있는 트레이스에는 이름에 '캘리퍼'를 포함하거나, 회전된 타원 홀/패드 위에 ROI를 배치하세요.\n② 복합 스코어링(First/Last 배제 + 목표 폭 친화도) 또는 방사형 스포크 Fitzgibbon + Sampson Gauss-Newton 타원(장축 A, 단축 B, 각도 θ)이 즉시 피팅됩니다.";

    public override string SampleSceneSlug => "ellipse";

    public override string SampleSceneTitle => "14. [본검사] 복합 스코어링 캘리퍼·Fitzgibbon 타원 피팅 샘플 (1200×800)";

    public override InspectionAlgorithmHelpSpec HelpSpec { get; } = new(
        "[정밀계측] 복합 스코어링 캘리퍼 & Fitzgibbon + Sampson Gauss-Newton 타원 피팅",
        "Cognex CogCaliperScoringFunction(대비·위치·목표폭 가우시안 친화도) + Fitzgibbon/Sampson 직교 거리 타원",
        "• 수식 원리:\n" +
        "  1) 복합 캘리퍼: 단순 최대 기울기 대신 S_pair = w_c·(|g_0|+|g_1|)/(2G_max) + w_p·PosScore + w_s·exp(-0.5·((|x_1-x_0|-W_0)/σ_W)²)를 평가하여 외곽 더미 가드 트레이스나 내부 실크 노이즈를 물리치고 정확한 목표 폭(W_0) 에지 쌍을 선택합니다.\n" +
        "  2) 정밀 타원 피팅: 경사 카메라나 드릴 각도로 인해 타원으로 투영된 홀에 방사형 스포크 캘리퍼를 쏘고, RANSAC으로 버(Burr)/칩핑 이상치를 제거한 뒤 Fitzgibbon 대수적 타원 초기해를 구하고 Sampson 거리 d_S(x,y) = F(x,y) / ‖∇F(x,y)‖에 대한 Levenberg-Marquardt / Gauss-Newton 반복으로 중심(X,Y), 장축(A), 단축(B), 회전각(θ)을 서브픽셀로 수렴시킵니다.",
        "① [타원 피팅] 원형/타원형 홀이나 패드 위에 ROI를 배치하면 36개 방사형 스포크가 서브픽셀 에지를 찾아 정밀 타원(초록 실선)과 장축·단축·각도를 즉시 피팅합니다.\n" +
        "② [복합 캘리퍼] ROI 이름에 '캘리퍼' 또는 '폭'을 포함하거나 세장형 트레이스 위에 배치하고 [비드/목표 폭]에 예상 폭(px)을 입력하면 간섭 에지를 배제한 진짜 폭을 계측합니다.",
        "• 비드/목표 폭 (Nominal Width, 3~120 px): 복합 캘리퍼가 우선적으로 찾을 목표 에지 간격(W_0)입니다.\n" +
        "• 가우시안 σ 및 최소 대비: 서브픽셀 에지 추출 필터의 감도를 조절합니다.\n" +
        "• 레이크/스포크 수 (8~48): 타원 둘레를 따라 몇 개의 방사형 스캔선을 쏠지 결정합니다.",
        "• 홀 가장자리 한쪽에 큰 버(Burr)나 이물이 있어도 RANSAC + Sampson 비선형 최적화가 이상치 스포크(빨강 점)를 자동 배제하고 실제 원/타원 경계를 복원합니다.");

    public override string FormatParameterSummary(InspectionSuiteParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        AdvancedMetrologyOptions opt = parameters.AdvancedMetrology;
        return $"복합 목표 폭={opt.CompositeExpectedSpanPx:F1}px · 타원 스포크={opt.EllipseSpokeCount}개 · 허용 RMS={opt.EllipseMaxRmsErrorPx:F2}px · σ={opt.GaussianSigma:F2} · 대비≥{opt.MinimumContrast:F1}";
    }

    public override ImmutableArray<ImmutableArray<PointD>> BuildGeometricGuides(
        InspectionSuiteRoiInput target,
        InspectionSuiteParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(parameters);

        AdvancedMetrologyOptions raw = parameters.AdvancedMetrology;
        var guides = ImmutableArray.CreateBuilder<ImmutableArray<PointD>>();

        if (target.ExecutionMode.AdvancedMetrology == AdvancedMetrologyVariant.CompositeCaliper)
        {
            double halfScan = Math.Max(MinGuideHalfScanPx, target.Width * GuideHalfScanWidthRatio);
            double halfProj = Math.Clamp(
                raw.CaliperProjectionWidth * HalfFactor,
                MinGuideHalfProjPx,
                Math.Max(MinGuideMaxHalfProjPx, target.Height * GuideMaxHalfProjHeightRatio));
            guides.Add([
                LocalToWorld(target, -halfScan, -halfProj),
                LocalToWorld(target, halfScan, -halfProj),
                LocalToWorld(target, halfScan, halfProj),
                LocalToWorld(target, -halfScan, halfProj)
            ]);
            guides.Add([LocalToWorld(target, -halfScan, 0), LocalToWorld(target, halfScan, 0)]);
        }
        else
        {
            double nomA = Math.Max(MinGuideNominalSemiMajorPx, target.Width * GuideNominalSemiMajorWidthRatio);
            double nomB = Math.Max(MinGuideNominalSemiMinorPx, target.Height * GuideNominalSemiMinorHeightRatio);
            int spokes = Math.Clamp(raw.CaliperRakeCount, AdvancedMetrologyOptions.MinGuideSpokes, AdvancedMetrologyOptions.MaxGuideSpokes);
            for (int s = 0; s < spokes; s++)
            {
                double t = 2.0 * Math.PI * s / spokes;
                double ct = Math.Cos(t);
                double st = Math.Sin(t);
                guides.Add([
                    LocalToWorld(target, nomA * GuideSpokeInnerScale * ct, nomB * GuideSpokeInnerScale * st),
                    LocalToWorld(target, nomA * GuideSpokeOuterScale * ct, nomB * GuideSpokeOuterScale * st)
                ]);
            }
        }

        return guides.ToImmutable();
    }

    public override ImmutableArray<InspectionSuiteItemResult> Execute(RoiInspectionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        AdvancedMetrologyOptions raw = context.Parameters.AdvancedMetrology;
        AdvancedMetrologyOptions validated = raw.Validate();
        int projStrips = Math.Clamp(
            (int)Math.Round(raw.CaliperProjectionWidth),
            AdvancedMetrologyOptions.MinProjectionStrips,
            AdvancedMetrologyOptions.MaxProjectionStrips);
        var state = new ValidatedAdvancedMetrologyState(raw, validated, projStrips);
        return ExecuteEach(context, CropMarginPixels, state, EvaluateTarget);
    }

    private InspectionSuiteItemResult EvaluateTarget(
        RoiInspectionContext context,
        InspectionSuiteRoiInput target,
        LocalRoiCrop localCrop,
        ValidatedAdvancedMetrologyState state,
        Func<double> measureElapsedMicroseconds)
    {
        var view = new ReadOnlyImageView2D<byte>(localCrop.Crop.Pixels.Span, localCrop.Crop.Width, localCrop.Crop.Height, localCrop.Crop.Stride);
        var localPose = new FixturePose2D(localCrop.LocalCenter.X, localCrop.LocalCenter.Y, (float)target.AngleRadians, 1.0f);

        if (target.ExecutionMode.AdvancedMetrology == AdvancedMetrologyVariant.CompositeCaliper)
        {
            return EvaluateCompositeCaliper(context, target, localCrop, view, localPose, state, measureElapsedMicroseconds);
        }

        return EvaluateEllipseFitting(context, target, localCrop, view, state, measureElapsedMicroseconds);
    }

    private InspectionSuiteItemResult EvaluateCompositeCaliper(
        RoiInspectionContext context,
        InspectionSuiteRoiInput target,
        LocalRoiCrop localCrop,
        ReadOnlyImageView2D<byte> view,
        FixturePose2D localPose,
        ValidatedAdvancedMetrologyState state,
        Func<double> measureElapsedMicroseconds)
    {
        AdvancedMetrologyOptions opt = state.ValidatedOptions;
        float scanLen = MathF.Max(MinCaliperScanLengthPx, (float)target.Width * CaliperScanWidthRatio);
        float projWidth = Math.Clamp(
            state.RawOptions.CaliperProjectionWidth,
            AdvancedMetrologyOptions.MinProjectionStrips,
            MathF.Max(MinDynamicProjectionHeightPx, (float)target.Height * DynamicProjectionHeightRatio));
        float expectedSpan = Math.Abs(opt.CompositeExpectedSpanPx - AdvancedMetrologyOptions.DefaultCompositeExpectedSpanPx) > DefaultExpectedSpanOverrideEpsilonPx
            ? opt.CompositeExpectedSpanPx
            : MathF.Max(MinAutoExpectedSpanPx, (float)target.Width * AutoExpectedSpanWidthRatio);

        var compSpec = CompositeCaliperSpec.Create(
            selectionMode: CaliperSelectionMode.CompositeScore,
            gaussianSigma: opt.GaussianSigma,
            minimumContrast: opt.MinimumContrast,
            projectionStripCount: state.ProjectionStripCount,
            expectedPosition: 0.0f,
            expectedSpan: expectedSpan,
            positionToleranceSigma: scanLen * PositionToleranceScanRatio,
            spanToleranceSigma: expectedSpan * SpanToleranceRatio);

        CaliperPairResult pair = CompositeSubpixelCaliper.MeasureEdgePair(
            view,
            localPose,
            scanLen,
            projWidth,
            CaliperEdgePolarity.DarkToLight,
            CaliperEdgePolarity.LightToDark,
            in compSpec,
            context.Workspace);

        var firstEdgeSpec = CompositeCaliperSpec.Create(CaliperSelectionMode.FirstEdge, opt.GaussianSigma, opt.MinimumContrast, state.ProjectionStripCount);
        var lastEdgeSpec = CompositeCaliperSpec.Create(CaliperSelectionMode.LastEdge, opt.GaussianSigma, opt.MinimumContrast, state.ProjectionStripCount);
        CaliperEdgeHit firstHit = CompositeSubpixelCaliper.MeasureSingleEdge(view, localPose, scanLen, projWidth, CaliperEdgePolarity.Any, in firstEdgeSpec, context.Workspace);
        CaliperEdgeHit lastHit = CompositeSubpixelCaliper.MeasureSingleEdge(view, localPose, scanLen, projWidth, CaliperEdgePolarity.Any, in lastEdgeSpec, context.Workspace);

        var builder = new InspectionItemResultBuilder(target)
            .WithGuideContours(BuildGeometricGuides(target, context.Parameters));

        if (pair.IsFound)
        {
            var p1 = new PointD(pair.FirstEdge.WorldPoint.X + localCrop.OffsetX, pair.FirstEdge.WorldPoint.Y + localCrop.OffsetY);
            var p2 = new PointD(pair.SecondEdge.WorldPoint.X + localCrop.OffsetX, pair.SecondEdge.WorldPoint.Y + localCrop.OffsetY);
            builder.AddPrimaryPoint(p1);
            builder.AddPrimaryPoint(p2);
            builder.AddContour([p1, p2]);
        }

        if (firstHit.GradientAmplitude > 0)
        {
            builder.AddSecondaryPoint(new PointD(firstHit.WorldPoint.X + localCrop.OffsetX, firstHit.WorldPoint.Y + localCrop.OffsetY));
        }

        if (lastHit.GradientAmplitude > 0)
        {
            builder.AddSecondaryPoint(new PointD(lastHit.WorldPoint.X + localCrop.OffsetX, lastHit.WorldPoint.Y + localCrop.OffsetY));
        }

        double elapsedUs = measureElapsedMicroseconds();
        bool isPass = pair.IsFound;
        string badge = isPass
            ? $"복합캘리퍼 폭={pair.SubpixelWidth:F3}px (First/Last 억제)"
            : "복합캘리퍼 미검출";
        string detail = isPass
            ? $"Composite 스코어링 폭={pair.SubpixelWidth:F3}px (목표폭={expectedSpan:F1}px, 좌/우 대비={pair.FirstEdge.GradientAmplitude:F1}/{pair.SecondEdge.GradientAmplitude:F1}) · 외곽 노이즈 에지 First u={firstHit.SubpixelPosition:F1}, Last u={lastHit.SubpixelPosition:F1} 배제 완료"
            : "복합 스코어링 조건에 부합하는 에지 쌍을 찾지 못했습니다.";

        return builder.Build(isPass, badge, detail, elapsedUs);
    }

    private InspectionSuiteItemResult EvaluateEllipseFitting(
        RoiInspectionContext context,
        InspectionSuiteRoiInput target,
        LocalRoiCrop localCrop,
        ReadOnlyImageView2D<byte> view,
        ValidatedAdvancedMetrologyState state,
        Func<double> measureElapsedMicroseconds)
    {
        AdvancedMetrologyOptions opt = state.ValidatedOptions;
        float nomA = MathF.Max(MinNominalSemiMajorPx, (float)target.Width * NominalSemiMajorWidthRatio);
        float nomB = MathF.Max(MinNominalSemiMinorPx, (float)target.Height * NominalSemiMinorHeightRatio);
        float radialScan = MathF.Max(MinRadialScanLengthPx, MathF.Max(nomA, nomB) * RadialScanAxisRatio);
        var calSpec = new CaliperMeasurementSpec(opt.GaussianSigma, opt.MinimumContrast, state.ProjectionStripCount);

        FittedEllipse2D ellipse = FitzgibbonEllipseFitter.MeasureSpokeEllipse(
            view,
            localCrop.LocalCenter,
            nomA,
            nomB,
            (float)target.AngleRadians,
            radialScan,
            opt.EllipseSpokeCount,
            CaliperEdgePolarity.Any,
            calSpec,
            context.Workspace);

        var builder = new InspectionItemResultBuilder(target)
            .WithGuideContours(BuildGeometricGuides(target, context.Parameters));

        if (ellipse.IsValid)
        {
            var worldCenter = new PointD(ellipse.Center.X + localCrop.OffsetX, ellipse.Center.Y + localCrop.OffsetY);
            builder.WithPose(target.Center, ellipse.AngleRad);
            builder.AddPrimaryPoint(worldCenter);
            builder.AddContour(BuildEllipsePolygon(worldCenter, ellipse.SemiMajorAxis, ellipse.SemiMinorAxis, ellipse.AngleRad, EllipsePolygonSegments));

            (double sinE, double cosE) = Math.SinCos(ellipse.AngleRad);
            builder.AddContour([
                new PointD(worldCenter.X - (ellipse.SemiMajorAxis * cosE), worldCenter.Y - (ellipse.SemiMajorAxis * sinE)),
                new PointD(worldCenter.X + (ellipse.SemiMajorAxis * cosE), worldCenter.Y + (ellipse.SemiMajorAxis * sinE))
            ]);
            builder.AddContour([
                new PointD(worldCenter.X + (ellipse.SemiMinorAxis * sinE), worldCenter.Y - (ellipse.SemiMinorAxis * cosE)),
                new PointD(worldCenter.X - (ellipse.SemiMinorAxis * sinE), worldCenter.Y + (ellipse.SemiMinorAxis * cosE))
            ]);
        }

        double elapsedUs = measureElapsedMicroseconds();
        bool isPass = ellipse.IsValid && ellipse.RmsGeometricErrorPixels < opt.EllipseMaxRmsErrorPx;
        double angleDeg = ellipse.AngleRad * RadiansToDegrees;
        string badge = ellipse.IsValid
            ? $"타원 A={ellipse.SemiMajorAxis:F2} · B={ellipse.SemiMinorAxis:F2}px · θ={angleDeg:F1}°"
            : "Fitzgibbon 타원 미검출";
        string detail = ellipse.IsValid
            ? $"Fitzgibbon + Sampson Gauss-Newton 타원 피팅: 중심=({ellipse.Center.X + localCrop.OffsetX:F2},{ellipse.Center.Y + localCrop.OffsetY:F2}), 장축 2A={ellipse.MajorDiameter:F2}px, 단축 2B={ellipse.MinorDiameter:F2}px, 회전={angleDeg:F2}°, 인라이어={ellipse.InlierCount}/{opt.EllipseSpokeCount}, 기하 RMS={ellipse.RmsGeometricErrorPixels:F4}px"
            : "방사형 스포크에서 유효한 타원 경계 에지를 충분히 검출하지 못했습니다.";

        return builder.Build(isPass, badge, detail, elapsedUs);
    }
}
