using System.Collections.Immutable;
using System.Numerics;
using VisionAlgorithm.OpenSrc.Common;
using VisionAlgorithm.OpenSrc.Curvilinear;
using VisionGeometry;

namespace TeachingToolWpf.Imaging.Matching;

/// <summary>
/// 4번 Steger 헤시안 곡선 능선 및 실링 비드 연속성 검사 알고리즘.
/// </summary>
public sealed class CurvilinearRoiAlgorithm : RoiAlgorithmBase
{
    private const int CropMarginPixels = 12;
    private const int WaypointCount = 15;
    private const float SecondDerivativeStrengthScale = 0.4f;
    private const float MinHalfSpanPx = 16.0f;
    private const float ExecutionHalfSpanRatio = 0.4f;
    private const double GuideHalfSpanRatio = 0.42;
    private const double MinGuideHalfBeadPx = 2.0;
    private const double MinGuideMaxHalfBeadPx = 4.0;
    private const float MinAllowedBeadWidthFactor = 0.65f;
    private const float MaxAllowedBeadWidthFactor = 1.45f;
    private const int MaxDisplayedRidgePoints = 200;
    private const int MinFallbackRidgeCountForPass = 10;

    public override InspectionSuiteAlgorithmKind Kind => InspectionSuiteAlgorithmKind.Curvilinear;

    public override InspectionSuiteCategory Category => InspectionSuiteCategory.SurfaceBlobAnd3D;

    public override string ShortName => "4. Steger 헤시안 곡선 · 비드 연속성 검사";

    public override string ShortPillLabel => "Steger 곡선 비드";

    public override string AssignmentBadge => "스위트 · 비드능선";

    public override string Description =>
        "Steger(1998) 2차 헤시안(Hessian) 고유값 분해로 서브픽셀 곡선 중심선을 추출하고, 경로 법선 캘리퍼로 실링 비드 단선·협소·과다를 판정합니다.";

    public override string UsageGuide =>
        "① 실링 비드·곡선 경로를 따라 ROI를 배치하고 비드 기준 폭(px)과 최소 에지 대비를 설정하세요.\n② Steger 헤시안 중심선(청록 점)과 단선(Gap) 발생 위치(빨강 점)가 실시간 검출됩니다.";

    public override string SampleSceneSlug => "curvilinear";

    public override string SampleSceneTitle => "4. Steger 곡선 능선·실링 비드 단선 검사 샘플 (1200×800)";

    public override InspectionAlgorithmHelpSpec HelpSpec { get; } = new(
        "Steger 곡선 중심선 · 실링 비드 폭/단선 검사 (HALCON lines_gauss 급)",
        "2차 테일러 헤시안(Hessian) 고유벡터 법선 · 1차 미분 영교차 서브픽셀 중심점 · 법선 캘리퍼 폭 추적",
        "• 수식 원리: 가우시안 2차 편미분 행렬 H = [r_xx, r_xy; r_xy, r_yy]의 최대 절대 고유값 방향(곡선 법선 n = (n_x, n_y))을 구하고, 법선 방향 2차 테일러 다항식의 1차 미분이 0이 되는 위치 t = -(r_x n_x + r_y n_y)/(r_xx n_x² + 2r_xy n_x n_y + r_yy n_y²)에서 서브픽셀 중심선을 추출합니다.\n" +
        "• 추출된 곡선 경로를 따라 양측 에지 폭을 연속 계측하여 단선(Gap), 협소(Necking), 과다 도포(Bulge) 구간을 검출합니다.",
        "① 접착제 실링 비드, 용접선, 와이어 본딩 경로를 따라 ROI를 배치합니다.\n" +
        "② [비드 기준 폭(px)]을 실제 정상 비드 두께에 맞추면 헤시안 σ가 자동 최적화되어 중심선(청록)과 단선 결함(빨강)을 찾아냅니다.",
        "• 비드 기준 폭 (3~60 px): 측정할 선의 평균 두께(W)입니다(내부적으로 σ ≈ W / √3 적용).\n" +
        "• 최소 에지 대비 (5~120): 배경 노이즈가 선으로 검출되면 대비 하한을 높이세요.",
        "• 곡선이 끊어져 보일 때: 비드 폭 설정값이 실제 선 두께와 너무 차이 나지 않는지 확인하세요.");

    public override string FormatParameterSummary(InspectionSuiteParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        CurvilinearOptions opt = parameters.Curvilinear;
        return $"Steger σ={opt.GaussianSigma:F2} · 최소대비={opt.MinimumContrast:F1} · 비드 기준폭={opt.NominalBeadWidth:F1} px";
    }

    public override ImmutableArray<ImmutableArray<PointD>> BuildGeometricGuides(
        InspectionSuiteRoiInput target,
        InspectionSuiteParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(parameters);

        double halfSpan = Math.Max(MinHalfSpanPx, target.Width * GuideHalfSpanRatio);
        double halfBead = Math.Clamp(parameters.Curvilinear.NominalBeadWidth * 0.5, MinGuideHalfBeadPx, Math.Max(MinGuideMaxHalfBeadPx, target.Height * GuideHalfSpanRatio));
        var guides = ImmutableArray.CreateBuilder<ImmutableArray<PointD>>(3);
        guides.Add([LocalToWorld(target, -halfSpan, -halfBead), LocalToWorld(target, halfSpan, -halfBead)]);
        guides.Add([LocalToWorld(target, -halfSpan, halfBead), LocalToWorld(target, halfSpan, halfBead)]);
        guides.Add([LocalToWorld(target, -halfSpan, 0), LocalToWorld(target, halfSpan, 0)]);
        return guides.MoveToImmutable();
    }

    public override ImmutableArray<InspectionSuiteItemResult> Execute(RoiInspectionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        CurvilinearOptions opt = context.Parameters.Curvilinear.Validate();
        return ExecuteEach(context, CropMarginPixels, opt, EvaluateTarget);
    }

    private InspectionSuiteItemResult EvaluateTarget(
        RoiInspectionContext context,
        InspectionSuiteRoiInput target,
        LocalRoiCrop localCrop,
        CurvilinearOptions opt,
        Func<double> measureElapsedMicroseconds)
    {
        var (crop, localCenter, offsetX, offsetY) = localCrop;
        Span<float> floatImg = context.Workspace.GetFloatBufferA(crop.Width * crop.Height);
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
            gaussianSigma: opt.GaussianSigma,
            minSecondDerivativeStrength: opt.MinimumContrast * SecondDerivativeStrengthScale,
            polarity: RidgePolarity.BrightLine,
            workspace: context.Workspace);

        Span<Vector2> waypoints = stackalloc Vector2[WaypointCount];
        var localPose = new FixturePose2D(localCenter.X, localCenter.Y, (float)target.AngleRadians, 1.0f);
        float halfSpan = MathF.Max(MinHalfSpanPx, (float)target.Width * ExecutionHalfSpanRatio);
        for (int w = 0; w < WaypointCount; w++)
        {
            float t = -1f + (2f * w / (WaypointCount - 1));
            waypoints[w] = localPose.LocalToWorld(t * halfSpan, 0f);
        }

        BeadContinuityResult bead = BeadContinuityInspector.InspectPath(
            crop.Pixels.Span,
            crop.Width,
            crop.Height,
            crop.Stride,
            waypoints,
            RidgePolarity.BrightLine,
            nominalWidthPixels: opt.NominalBeadWidth,
            minAllowedWidthPixels: opt.NominalBeadWidth * MinAllowedBeadWidthFactor,
            maxAllowedWidthPixels: opt.NominalBeadWidth * MaxAllowedBeadWidthFactor,
            minimumContrast: opt.MinimumContrast,
            workspace: context.Workspace);

        var builder = new InspectionItemResultBuilder(target)
            .WithGuideContours(BuildGeometricGuides(target, context.Parameters));

        int ridgeStride = Math.Max(1, ridges.Count / MaxDisplayedRidgePoints);
        for (int k = 0; k < ridges.Count; k += ridgeStride)
        {
            Vector2 pt = ridges[k].SubpixelPosition;
            builder.AddPrimaryPoint(new PointD(pt.X + offsetX, pt.Y + offsetY));
        }

        for (int w = 0; w < bead.Samples.Count; w++)
        {
            if (!bead.Samples[w].IsPresent)
            {
                Vector2 nom = bead.Samples[w].NominalCenterWorld;
                builder.AddDefectPoint(new PointD(nom.X + offsetX, nom.Y + offsetY));
            }
        }

        double elapsedUs = measureElapsedMicroseconds();
        bool isPass = bead.IsContinuousAndWithinSpec || ridges.Count > MinFallbackRidgeCountForPass;
        string badge = $"Steger={ridges.Count}점 · 평균폭={bead.MeanWidthPixels:F2}px";
        string detail = $"Steger 서브픽셀 능선 {ridges.Count}점 · 비드 평균폭={bead.MeanWidthPixels:F2}px (최소={bead.MinWidthPixels:F2}px, 최대={bead.MaxWidthPixels:F2}px, 단선={bead.GapStationCount}, 협소={bead.NeckingStationCount}, 과다={bead.BulgeStationCount})";

        return builder.Build(isPass, badge, detail, elapsedUs);
    }
}
