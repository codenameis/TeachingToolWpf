using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.InteropServices;
using VisionAlgorithm.OpenSrc.Common;
using VisionAlgorithm.OpenSrc.Metrology;
using VisionGeometry;

namespace TeachingToolWpf.Imaging.Matching;

/// <summary>
/// 1번 서브픽셀 캘리퍼 · RANSAC 직선 · Taubin 원 계측 알고리즘.
/// </summary>
public sealed class MetrologyRoiAlgorithm : RoiAlgorithmBase
{
    private const int CropMarginPixels = 32;
    private const float MinScanLengthPx = 24.0f;
    private const float ScanWidthRatio = 0.90f;
    private const float MinDynamicProjectionHeightPx = 5.0f;
    private const float DynamicProjectionHeightRatio = 0.85f;
    private const float MinHalfHeightPx = 12.0f;
    private const float HalfHeightRatio = 0.42f;
    private const float MinNominalRadiusPx = 8.0f;
    private const float NominalRadiusRatio = 0.40f;
    private const float MinRadialScanLengthPx = 16.0f;
    private const float RadialScanRadiusRatio = 0.78f;
    private const float CircleAutoDominanceMaxRmsPx = 1.2f;
    private const double MinEdgeTickHalfPx = 4.0;
    private const double MaxEdgeTickHalfPx = 22.0;
    private const int MinValidRakeLineHits = 3;
    private const float LineInlierDistanceThresholdPx = 1.5f;
    private const int CirclePolygonSegments = 48;
    private const double CircleCrosshairHalfLengthPx = 6.0;
    private const float ToothProjectionWidthPx = 5.0f;
    private const double GuideHalfScanRatio = 0.45;
    private const double GuideHalfHeightRatio = 0.42;
    private const double GuideNominalRadiusRatio = 0.40;
    private const double GuideRadialScanRadiusRatio = 0.78;
    private const double MinGuideHalfHeightPx = 12.0;
    private const double MinGuideNominalRadiusPx = 8.0;
    private const double MinGuideRadialScanLengthPx = 16.0;
    private const double MinGuideHalfProjPx = 2.5;
    private const double MinGuideMaxHalfProjPx = 4.0;
    private const double GuideArrowScale = 0.14;
    private const double MinGuideArrowSizePx = 4.0;
    private const double MaxGuideArrowSizePx = 10.0;
    private const double GuideArrowWingAspect = 0.6;
    private const double MinInnerGuideRadiusPx = 3.0;
    private const int GuideInnerCircleSegments = 36;
    private const int GuideOuterCircleSegments = 44;
    private const double RadiansToDegrees = 180.0 / Math.PI;

    private readonly record struct ValidatedMetrologyState(
        MetrologyOptions Options,
        CaliperMeasurementSpec Spec,
        CaliperEdgePolarity FirstPolarity,
        CaliperEdgePolarity SecondPolarity,
        CaliperEdgePolarity SinglePolarity,
        int RakeStations,
        int SpokeCount);

    public override InspectionSuiteAlgorithmKind Kind => InspectionSuiteAlgorithmKind.Metrology;

    public override InspectionSuiteCategory Category => InspectionSuiteCategory.MetrologyAndGeometry;

    public override string ShortName => "1. 서브픽셀 캘리퍼 · RANSAC 직선 · Taubin 원 계측";

    public override string ShortPillLabel => "1D 캘리퍼·직선·원";

    public override string AssignmentBadge => "스위트 · 캘리퍼";

    public override string Description =>
        "1D 가우시안 1차 미분 + 포물선 서브픽셀 에지 폭 계측, 다중 캘리퍼(Rake) RANSAC+IRLS 직선 피팅, 방사형 스포크 Taubin 원 피팅을 수행합니다.";

    public override string UsageGuide =>
        "① 상단 '현재 알고리즘 샘플 열기'를 누르거나 캔버스에서 측정할 위치에 ROI를 배치·회전하세요.\n② 계측 모드(자동 / 1D 폭 / 레이크 직선 / 스포크 원)와 시그마(σ)·최소 대비 슬라이더를 움직이면 캔버스에 스캔 가이드와 계측 결과가 실시간 프리뷰됩니다.";

    public override string SampleSceneSlug => "metrology";

    public override string SampleSceneTitle => "1. 서브픽셀 캘리퍼·직선·원 계측 합성 샘플 (1200×800)";

    public override InspectionAlgorithmHelpSpec HelpSpec { get; } = new(
        "서브픽셀 캘리퍼 · 기하 계측 (1D Edge Pair / Rake Line / Spoke Circle)",
        "1D 가우시안 미분 투영 · 포물선 3점 서브픽셀 피크 · RANSAC + Huber IRLS 직선 · Taubin 대수 원 피팅",
        "• 수식 원리: ROI 스캔 방향에 수직으로 픽셀 밝기를 투영 적분(Projection Width)하여 노이즈를 1/√W로 억제한 뒤, 1D 가우시안 1차 미분 커널 G'_σ(x) = -(x/σ²)exp(-x²/2σ²)와 합성곱합니다.\n" +
        "• 극값 전후 3점 포물선 보간 δ = (g(-1) - g(+1)) / (2·(g(-1) - 2g(0) + g(+1)))으로 ±0.02 px 서브픽셀 에지를 찾고, 다중 레이크/스포크 점들을 RANSAC + IRLS 직선 또는 Taubin 원 방정식으로 피팅합니다.",
        "① [▶ 현재 선택 알고리즘 전용 샘플 영상 불러오기]를 누르거나 측정할 경계에 ROI를 배치합니다.\n" +
        "② [캘리퍼 계측 모드]에서 자동 / 1D 양측 폭 / 다중 레이크 직선 / 방사형 스포크 원 중 목적에 맞는 모드를 고릅니다.\n" +
        "③ ROI를 회전·이동하면 하늘색 스캔 가이드와 검출 에지(청록 점), 폭/직선/원 피팅 결과가 실시간으로 표시됩니다.",
        "• 캘리퍼 레이크 수 (5~48): 직선/원 피팅 시 스캔 라인 개수입니다. 칩핑이나 노이즈가 많으면 15~25개로 늘리세요.\n" +
        "• 투영 적분 폭 (3~45 px): 거친 금속 표면일수록 투영 폭을 11~21 px로 넓혀 스펙클 노이즈를 평활화하세요.\n" +
        "• 가우시안 σ (0.5~5.0): 칼날처럼 선명한 에지는 0.8~1.2, 흐릿한 경계는 1.8~3.0으로 설정하세요.",
        "• 엉뚱한 내부 노이즈 에지가 잡힐 때: [에지 극성 모드]를 '밝은 띠(BrightStripe)' 또는 '어두운 슬롯(DarkSlot)'으로 명시하고 최소 에지 대비를 높이세요.");

    public override string GetUsageGuide(InspectionSuiteParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return parameters.Metrology.Mode switch
        {
            MetrologyMeasurementMode.EdgePairWidth =>
                "① [1D 폭 계측] 캔버스에서 트레이스·슬롯 위에 ROI를 올리고, ROI 방향 화살표가 양쪽 에지를 수직으로 가로지르도록 회전하세요.\n② 하늘색 점선 박스(투영 적분 영역)와 화살표가 실시간 표시되며, 검출된 좌·우 에지(청록 점)와 서브픽셀 폭(초록 선)이 즉시 계산됩니다.",
            MetrologyMeasurementMode.RakeLineFit =>
                "① [다중 레이크 직선 피팅] 측정할 직선 경계선을 따라 ROI를 배치하고 화살표가 에지에 수직이 되도록 맞추세요.\n② 레이크 분할 수만큼 평행 스캔 보조선이 표시되며, 이상치(빨강 점)를 제외한 인라이어(청록 점)로 RANSAC+IRLS 최적 직선을 피팅합니다.",
            MetrologyMeasurementMode.SpokeCircleFit =>
                "① [방사형 스포크 원 피팅] 원형 패드·홀 중심에 정사각형 ROI를 배치하세요 (내·외측 하늘색 점선 링 사이에 원 경계가 들어오도록 크기 조절).\n② 중심에서 퍼져 나가는 스포크 보조선마다 서브픽셀 에지를 찾아 Taubin 대수적 원(반지름 R, 외경 Ø, 진원도 RMS)을 피팅합니다.",
            _ => UsageGuide
        };
    }

    public override string FormatParameterSummary(InspectionSuiteParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        MetrologyOptions opt = parameters.Metrology;
        return $"모드={opt.Mode} · 극성={opt.PolarityMode} · σ={opt.GaussianSigma:F2} · 대비≥{opt.MinimumContrast:F1} · 레이크/스포크={opt.CaliperRakeCount} · 투영폭={opt.CaliperProjectionWidth:F0}px";
    }

    public override ImmutableArray<ImmutableArray<PointD>> BuildGeometricGuides(
        InspectionSuiteRoiInput target,
        InspectionSuiteParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(parameters);

        MetrologyOptions opt = parameters.Metrology;
        MetrologyMeasurementMode mode = opt.Mode == MetrologyMeasurementMode.Auto
            ? (target.ExecutionMode.MetrologyDefaultMode == MetrologyMeasurementMode.SpokeCircleFit
                ? MetrologyMeasurementMode.SpokeCircleFit
                : MetrologyMeasurementMode.EdgePairWidth)
            : opt.Mode;

        return BuildGuidesForResolvedMode(target, opt, mode);
    }

    public override ImmutableArray<InspectionSuiteItemResult> Execute(RoiInspectionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        MetrologyOptions opt = context.Parameters.Metrology.Validate();
        int projStrips = Math.Clamp((int)Math.Round(context.Parameters.Metrology.CaliperProjectionWidth), (int)MetrologyOptions.MinProjectionWidth, (int)MetrologyOptions.MaxProjectionWidth);
        var spec = new CaliperMeasurementSpec(
            gaussianSigma: opt.GaussianSigma,
            minimumContrast: opt.MinimumContrast,
            projectionStripCount: projStrips);

        var (firstPolarity, secondPolarity, singlePolarity) = ResolvePolarities(opt.PolarityMode);
        int rakeStations = Math.Clamp(context.Parameters.Metrology.CaliperRakeCount, MetrologyOptions.MinRakeCount, MetrologyOptions.MaxRakeCount);
        int spokeCount = Math.Clamp(context.Parameters.Metrology.CaliperRakeCount, MetrologyOptions.MinSpokeCount, MetrologyOptions.MaxSpokeCount);

        var state = new ValidatedMetrologyState(opt, spec, firstPolarity, secondPolarity, singlePolarity, rakeStations, spokeCount);
        return ExecuteEach(context, CropMarginPixels, state, EvaluateTarget);
    }

    private InspectionSuiteItemResult EvaluateTarget(
        RoiInspectionContext context,
        InspectionSuiteRoiInput target,
        LocalRoiCrop localCrop,
        ValidatedMetrologyState state,
        Func<double> measureElapsedMicroseconds)
    {
        var (crop, localCenter, offsetX, offsetY) = localCrop;
        var localPose = new FixturePose2D(localCenter.X, localCenter.Y, (float)target.AngleRadians, 1.0f);

        float scanLen = MathF.Max(MinScanLengthPx, (float)target.Width * ScanWidthRatio);
        float projWidth = Math.Clamp(context.Parameters.Metrology.CaliperProjectionWidth, MetrologyOptions.MinProjectionWidth, MathF.Max(MinDynamicProjectionHeightPx, (float)target.Height * DynamicProjectionHeightRatio));
        float halfHeight = MathF.Max(MinHalfHeightPx, (float)target.Height * HalfHeightRatio);
        float nominalRadius = MathF.Max(MinNominalRadiusPx, MathF.Min((float)target.Width, (float)target.Height) * NominalRadiusRatio);
        float radialScanLen = MathF.Max(MinRadialScanLengthPx, nominalRadius * RadialScanRadiusRatio);

        MetrologyMeasurementMode effectiveMode = state.Options.Mode;
        bool isCirclePreferred = target.ExecutionMode.MetrologyDefaultMode == MetrologyMeasurementMode.SpokeCircleFit;

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
                firstPolarity: state.FirstPolarity,
                secondPolarity: state.SecondPolarity,
                spec: state.Spec,
                workspace: context.Workspace);
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
                state.RakeStations,
                state.SinglePolarity,
                state.Spec,
                context.Workspace);
        }

        FittedCircle2D circle = default;
        List<Vector2> spokeHits = [];
        if (runCircle)
        {
            CaliperEdgePolarity radialPolarity = state.SinglePolarity == CaliperEdgePolarity.DarkToLight
                ? CaliperEdgePolarity.DarkToLight
                : state.SinglePolarity == CaliperEdgePolarity.LightToDark
                    ? CaliperEdgePolarity.LightToDark
                    : CaliperEdgePolarity.Any;

            circle = TaubinCircleFitter.MeasureSpokeCircle(
                crop.Pixels.Span,
                crop.Width,
                crop.Height,
                crop.Stride,
                localCenter,
                nominalRadius: nominalRadius,
                radialScanLength: radialScanLen,
                spokeCount: state.SpokeCount,
                radialPolarity: radialPolarity,
                caliperSpec: state.Spec,
                workspace: context.Workspace);

            spokeHits = CollectSpokeEdgePoints(
                crop.Pixels.Span,
                crop.Width,
                crop.Height,
                crop.Stride,
                localCenter,
                nominalRadius,
                radialScanLen,
                state.SpokeCount,
                state.SinglePolarity,
                state.Spec,
                context.Workspace);
        }

        MetrologyMeasurementMode guideMode = effectiveMode;
        if (effectiveMode == MetrologyMeasurementMode.Auto)
        {
            bool circleDominant = isCirclePreferred || (circle.IsValid && circle.RmsRadialErrorPixels < CircleAutoDominanceMaxRmsPx && !pair.IsFound);
            guideMode = circleDominant ? MetrologyMeasurementMode.SpokeCircleFit : MetrologyMeasurementMode.EdgePairWidth;
        }

        var builder = new InspectionItemResultBuilder(target)
            .WithGuideContours(BuildGuidesForResolvedMode(target, context.Parameters.Metrology, guideMode));

        double cos = Math.Cos(target.AngleRadians);
        double sin = Math.Sin(target.AngleRadians);
        PointD normalDir = new(-sin, cos);
        double tickHalf = Math.Clamp(projWidth * 0.5, MinEdgeTickHalfPx, MaxEdgeTickHalfPx);

        if (runPair && pair.IsFound && (effectiveMode != MetrologyMeasurementMode.Auto || !isCirclePreferred))
        {
            PointD p1 = new(pair.FirstEdge.WorldPoint.X + offsetX, pair.FirstEdge.WorldPoint.Y + offsetY);
            PointD p2 = new(pair.SecondEdge.WorldPoint.X + offsetX, pair.SecondEdge.WorldPoint.Y + offsetY);
            builder.AddPrimaryPoint(p1)
                .AddPrimaryPoint(p2)
                .AddContour([p1, p2])
                .AddContour([
                    new PointD(p1.X - (normalDir.X * tickHalf), p1.Y - (normalDir.Y * tickHalf)),
                    new PointD(p1.X + (normalDir.X * tickHalf), p1.Y + (normalDir.Y * tickHalf))
                ])
                .AddContour([
                    new PointD(p2.X - (normalDir.X * tickHalf), p2.Y - (normalDir.Y * tickHalf)),
                    new PointD(p2.X + (normalDir.X * tickHalf), p2.Y + (normalDir.Y * tickHalf))
                ]);
        }

        string lineSummary = "직선 미검출";
        double lineAngleDeg = 0.0;
        if (runRake && rakeHits.Count >= MinValidRakeLineHits && (effectiveMode != MetrologyMeasurementMode.Auto || !isCirclePreferred) && fittedLine.IsValid)
        {
            Vector2 lineStart = fittedLine.Centroid - (fittedLine.Direction * halfHeight);
            Vector2 lineEnd = fittedLine.Centroid + (fittedLine.Direction * halfHeight);
            builder.AddContour([
                new PointD(lineStart.X + offsetX, lineStart.Y + offsetY),
                new PointD(lineEnd.X + offsetX, lineEnd.Y + offsetY)
            ]);
            lineAngleDeg = Math.Atan2(fittedLine.Direction.Y, fittedLine.Direction.X) * RadiansToDegrees;
            lineSummary = $"직선 {lineAngleDeg:F2}° (인라이어 {fittedLine.InlierCount}/{rakeHits.Count}, RMS {fittedLine.RmsResidualPixels:F3}px)";

            for (int k = 0; k < rakeHits.Count; k++)
            {
                Vector2 pt = rakeHits[k];
                float dist = MathF.Abs(Vector2.Dot(pt - fittedLine.Centroid, fittedLine.Normal));
                PointD worldPt = new(pt.X + offsetX, pt.Y + offsetY);
                if (dist <= LineInlierDistanceThresholdPx)
                {
                    if (effectiveMode == MetrologyMeasurementMode.RakeLineFit)
                    {
                        builder.AddPrimaryPoint(worldPt);
                    }
                    else
                    {
                        builder.AddSecondaryPoint(worldPt);
                    }
                }
                else
                {
                    builder.AddDefectPoint(worldPt);
                }
            }
        }

        string circleSummary = "원 미검출";
        if (runCircle && circle.IsValid && (effectiveMode != MetrologyMeasurementMode.Auto || isCirclePreferred || !pair.IsFound))
        {
            PointD worldCircleCenter = new(circle.Center.X + offsetX, circle.Center.Y + offsetY);
            builder.AddContour(BuildCirclePolygon(worldCircleCenter, circle.Radius, CirclePolygonSegments))
                .AddContour([new PointD(worldCircleCenter.X - CircleCrosshairHalfLengthPx, worldCircleCenter.Y), new PointD(worldCircleCenter.X + CircleCrosshairHalfLengthPx, worldCircleCenter.Y)])
                .AddContour([new PointD(worldCircleCenter.X, worldCircleCenter.Y - CircleCrosshairHalfLengthPx), new PointD(worldCircleCenter.X, worldCircleCenter.Y + CircleCrosshairHalfLengthPx)]);
            circleSummary = $"Taubin 원 R={circle.Radius:F3}px (Ø={circle.Radius * 2f:F2}px, RMS {circle.RmsRadialErrorPixels:F3}px)";

            for (int k = 0; k < spokeHits.Count; k++)
            {
                PointD worldSpokePt = new(spokeHits[k].X + offsetX, spokeHits[k].Y + offsetY);
                if (effectiveMode == MetrologyMeasurementMode.SpokeCircleFit || isCirclePreferred)
                {
                    builder.AddPrimaryPoint(worldSpokePt);
                }
                else
                {
                    builder.AddSecondaryPoint(worldSpokePt);
                }
            }
        }

        double elapsedUs = measureElapsedMicroseconds();
        var (isPass, badge, detail) = FormatVerdict(
            effectiveMode,
            isCirclePreferred,
            pair,
            fittedLine,
            circle,
            rakeHits.Count,
            state.RakeStations,
            state.SpokeCount,
            projWidth,
            lineAngleDeg,
            lineSummary,
            circleSummary,
            offsetX,
            offsetY);

        return builder.Build(isPass, badge, detail, elapsedUs);
    }

    private static (bool IsPass, string Badge, string Detail) FormatVerdict(
        MetrologyMeasurementMode effectiveMode,
        bool isCirclePreferred,
        CaliperPairResult pair,
        FittedLine2D fittedLine,
        FittedCircle2D circle,
        int rakeHitCount,
        int rakeStations,
        int spokeCount,
        float projWidth,
        double lineAngleDeg,
        string lineSummary,
        string circleSummary,
        int offsetX,
        int offsetY)
    {
        string widthText = pair.IsFound ? $"폭={pair.SubpixelWidth:F3}px" : "폭 미검출";

        switch (effectiveMode)
        {
            case MetrologyMeasurementMode.EdgePairWidth:
            {
                bool isPass = pair.IsFound;
                string badge = pair.IsFound ? $"1D {widthText}" : "1D 폭 미검출";
                string detail = pair.IsFound
                    ? $"1D 캘리퍼 {widthText} (좌에지 대비={pair.FirstEdge.GradientAmplitude:F1}, 우에지 대비={pair.SecondEdge.GradientAmplitude:F1}, 투영폭={projWidth:F0}px)"
                    : "1D 양측 에지 쌍 미검출 — ROI 화살표가 측정할 양쪽 경계를 가로지르도록 배치·회전하거나 최소 대비를 낮추세요.";
                return (isPass, badge, detail);
            }

            case MetrologyMeasurementMode.RakeLineFit:
            {
                bool isPass = fittedLine.IsValid && fittedLine.InlierCount >= MinValidRakeLineHits;
                string badge = isPass ? $"레이크 직선 {lineAngleDeg:F2}° (RMS {fittedLine.RmsResidualPixels:F3}px)" : "레이크 직선 미검출";
                string detail = isPass
                    ? $"다중 캘리퍼 레이크({rakeStations}개) {lineSummary}"
                    : $"다중 캘리퍼 레이크 직선 미검출 (검출 에지 {rakeHitCount}/{rakeStations}개) — ROI 화살표를 에지에 수직으로 맞추세요.";
                return (isPass, badge, detail);
            }

            case MetrologyMeasurementMode.SpokeCircleFit:
            {
                bool isPass = circle.IsValid;
                string badge = circle.IsValid ? $"스포크 원 R={circle.Radius:F2}px (Ø={circle.Radius * 2f:F2}px)" : "스포크 원 미검출";
                string detail = circle.IsValid
                    ? $"방사형 스포크({spokeCount}개) {circleSummary}, 중심=({circle.Center.X + offsetX:F2}, {circle.Center.Y + offsetY:F2})"
                    : "방사형 스포크 원 미검출 — 원형 홀/패드 중심에 정사각형 ROI를 맞추고 반지름이 ROI 크기의 약 35~45%가 되도록 조절하세요.";
                return (isPass, badge, detail);
            }

            default:
            {
                bool isPass = pair.IsFound || circle.IsValid || fittedLine.IsValid;
                if (isCirclePreferred && circle.IsValid)
                {
                    return (
                        isPass,
                        $"원 R={circle.Radius:F2}px (Ø={circle.Radius * 2f:F2}px)",
                        $"자동 계측(원형) · {circleSummary} · 1D {widthText}");
                }

                string badge = pair.IsFound ? $"{widthText} · 직선 {(fittedLine.IsValid ? $"{lineAngleDeg:F1}°" : "-")}" : circleSummary;
                string detail = $"1D 캘리퍼 {widthText} · {lineSummary}" + (circle.IsValid ? $" · {circleSummary}" : string.Empty);
                return (isPass, badge, detail);
            }
        }
    }

    private static ImmutableArray<ImmutableArray<PointD>> BuildGuidesForResolvedMode(
        InspectionSuiteRoiInput target,
        MetrologyOptions opt,
        MetrologyMeasurementMode mode)
    {
        var guides = ImmutableArray.CreateBuilder<ImmutableArray<PointD>>();
        double halfScan = Math.Max(MinGuideHalfHeightPx, target.Width * GuideHalfScanRatio);

        if (mode == MetrologyMeasurementMode.EdgePairWidth)
        {
            double halfProj = Math.Clamp(opt.CaliperProjectionWidth * 0.5, MinGuideHalfProjPx, Math.Max(MinGuideMaxHalfProjPx, target.Height * GuideHalfHeightRatio));
            guides.Add([
                LocalToWorld(target, -halfScan, -halfProj),
                LocalToWorld(target, halfScan, -halfProj),
                LocalToWorld(target, halfScan, halfProj),
                LocalToWorld(target, -halfScan, halfProj)
            ]);
            guides.Add([LocalToWorld(target, -halfScan, 0), LocalToWorld(target, halfScan, 0)]);
            double arrowSize = Math.Clamp(halfScan * GuideArrowScale, MinGuideArrowSizePx, MaxGuideArrowSizePx);
            guides.Add([LocalToWorld(target, halfScan - arrowSize, -arrowSize * GuideArrowWingAspect), LocalToWorld(target, halfScan, 0)]);
            guides.Add([LocalToWorld(target, halfScan - arrowSize, arrowSize * GuideArrowWingAspect), LocalToWorld(target, halfScan, 0)]);
        }
        else if (mode == MetrologyMeasurementMode.RakeLineFit)
        {
            int rakeCount = Math.Clamp(opt.CaliperRakeCount, MetrologyOptions.MinRakeCount, MetrologyOptions.MaxGuideRakeCount);
            double halfHeight = Math.Max(MinGuideHalfHeightPx, target.Height * GuideHalfHeightRatio);
            for (int r = 0; r < rakeCount; r++)
            {
                double t = rakeCount == 1 ? 0.0 : (-1.0 + (2.0 * r / (rakeCount - 1)));
                double ly = t * halfHeight;
                guides.Add([LocalToWorld(target, -halfScan, ly), LocalToWorld(target, halfScan, ly)]);
            }

            double arrowSize = Math.Clamp(halfScan * GuideArrowScale, MinGuideArrowSizePx, MaxGuideArrowSizePx);
            guides.Add([LocalToWorld(target, halfScan - arrowSize, -arrowSize * GuideArrowWingAspect), LocalToWorld(target, halfScan, 0)]);
            guides.Add([LocalToWorld(target, halfScan - arrowSize, arrowSize * GuideArrowWingAspect), LocalToWorld(target, halfScan, 0)]);
        }
        else if (mode == MetrologyMeasurementMode.SpokeCircleFit)
        {
            PointD center = target.Center;
            double nominalRadius = Math.Max(MinGuideNominalRadiusPx, Math.Min(target.Width, target.Height) * GuideNominalRadiusRatio);
            double radialScan = Math.Max(MinGuideRadialScanLengthPx, nominalRadius * GuideRadialScanRadiusRatio);
            double innerR = Math.Max(MinInnerGuideRadiusPx, nominalRadius - (radialScan * 0.5));
            double outerR = nominalRadius + (radialScan * 0.5);
            guides.Add(BuildCirclePolygon(center, innerR, GuideInnerCircleSegments));
            guides.Add(BuildCirclePolygon(center, outerR, GuideOuterCircleSegments));

            int spokes = Math.Clamp(opt.CaliperRakeCount, MetrologyOptions.MinSpokeCount, MetrologyOptions.MaxGuideSpokeCount);
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

        return guides.ToImmutable();
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
            var line = hits.Count >= MinValidRakeLineHits
                ? RansacIrlsLineFitter.FitLine(CollectionsMarshal.AsSpan(hits), LineInlierDistanceThresholdPx, workspace)
                : default;
            return (hits, line);
        }

        // When Any polarity is selected on a symmetric stripe, evaluate DarkToLight and LightToDark separately
        // so individual rake teeth do not alternate between the left and right edges of the stripe.
        var risingHits = CollectRakeHits(image, width, height, stride, localPose, scanLen, halfHeight, rakeStations, CaliperEdgePolarity.DarkToLight, spec, workspace);
        var fallingHits = CollectRakeHits(image, width, height, stride, localPose, scanLen, halfHeight, rakeStations, CaliperEdgePolarity.LightToDark, spec, workspace);

        FittedLine2D risingLine = risingHits.Count >= MinValidRakeLineHits
            ? RansacIrlsLineFitter.FitLine(CollectionsMarshal.AsSpan(risingHits), LineInlierDistanceThresholdPx, workspace)
            : default;
        FittedLine2D fallingLine = fallingHits.Count >= MinValidRakeLineHits
            ? RansacIrlsLineFitter.FitLine(CollectionsMarshal.AsSpan(fallingHits), LineInlierDistanceThresholdPx, workspace)
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
                projectionWidthPixels: ToothProjectionWidthPx,
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
                projectionWidthPixels: ToothProjectionWidthPx,
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
}
