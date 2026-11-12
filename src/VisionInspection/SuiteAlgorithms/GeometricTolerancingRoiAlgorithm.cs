using System.Collections.Immutable;
using VisionAlgorithm.OpenSrc.Metrology;
using VisionGeometry;

namespace TeachingToolWpf.Imaging.Matching;

/// <summary>
/// 20번 다중 형상 서브픽셀 기하공차(GD&amp;T) 직교도·동심도·데이텀 수직거리 계측 알고리즘.
/// </summary>
public sealed class GeometricTolerancingRoiAlgorithm : RoiAlgorithmBase
{
    private const int CropMarginPixels = 8;
    private const double NominalPerpendicularAngleDeg = 90.0;
    private const double NominalCenterDistancePx = 0.0;
    private const int OuterCirclePolygonSegments = 36;
    private const int InnerCirclePolygonSegments = 28;

    public override InspectionSuiteAlgorithmKind Kind => InspectionSuiteAlgorithmKind.GeometricTolerancing;

    public override InspectionSuiteCategory Category => InspectionSuiteCategory.MetrologyAndGeometry;

    public override string ShortName => "20. [본검사] 다중 형상 기하공차(GD&T) 직교도·동심도·데이텀 수직거리 계측";

    public override string ShortPillLabel => "다중형상 GD&T 공차";

    public override string AssignmentBadge => "스위트 · GD&T공차";

    public override string Description =>
        "다중 형상 서브픽셀 기하공차(GD&T): 수평/수직 데이텀 직선 직교도(⊥), 내/외경 이중 보어 동심도(◎ Δc), 보어-데이텀 수직거리를 정밀 계측합니다.";

    public override string UsageGuide =>
        "① 부품의 상단/좌측 데이텀 모서리와 중앙 동심 보어 홀을 감싸도록 ROI를 배치하세요.\n② 데이텀 직교각(°), 내/외경 동심도 편차(Δc px), 데이텀 수직거리 및 공차 합격 여부를 즉시 계측합니다.";

    public override string SampleSceneSlug => "geomtolerance";

    public override string SampleSceneTitle => "20. [본검사] 다중 형상 기하공차(GD&T) 직교도·동심도·수직거리 샘플 (1200×800)";

    public override InspectionAlgorithmHelpSpec HelpSpec { get; } = new(
        "[GD&T 기하공차] 다중 형상 서브픽셀 직교도(⊥) · 동심도(◎) · 데이텀 수직거리 정밀 계측",
        "수평/수직 데이텀 직선 Tukey IRLS 피팅 · 내/외경 보어 Kåsa-Tukey 원 피팅 · 직교각 Δθ & 동심도 Δc",
        "• 수식 원리:\n" +
        "  1) 데이텀 직교도(Perpendicularity ⊥): 부품 상단 데이텀 직선(n₁·x + d₁ = 0)과 좌측 데이텀 직선(n₂·x + d₂ = 0)을 서브픽셀 캘리퍼 + Tukey Biweight IRLS로 피팅하고, 교차각 θ = arccos(|n₁·n₂|) 및 기준 코너 교차점(x_×, y_×)을 계산합니다.\n" +
        "  2) 원-원 동심도(Concentricity ◎): 중앙 이중 보어의 내경 원(c_in, R_in)과 외경 원(c_out, R_out)을 36-스포크 서브픽셀 에지로 피팅하여 중심 이탈 거리 Δc = ‖c_out - c_in‖₂를 소수점 3자리(px)로 산출합니다.\n" +
        "  3) 보어-데이텀 수직 거리: 외경 중심에서 수평/수직 데이텀 직선까지의 정밀 수직 거리 d_⊥ = |n·c_out + d|를 계측합니다.",
        "① 직교 데이텀 모서리와 동심 보어 홀을 가진 정밀 가공 브래킷 전체를 감싸도록 ROI를 배치합니다 (샘플 좌측: 직교 90.0°·동심도 0.0px 정상 부품 / 우측: 경사 2.6°·편심 4.4px 불량 부품).\n" +
        "② [선택 ROI 실행]을 누르면 직교각, 동심도 편차(Δc), 데이텀 수직거리 및 공차 합격 여부가 캔버스 기하 오버레이와 함께 즉시 출력됩니다.",
        "• 직각 허용 오차 (±°, 0.5~15°): 데이텀 직교각(90°) 허용 공차 범위입니다.\n" +
        "• 최소 에지 대비: 데이텀 및 보어 서브픽셀 에지 검출 문턱값입니다.",
        "• 단일 치수만 재는 일반 캘리퍼와 달리, 복수의 기하 피처(직선 2개 + 동심원 2개) 간의 상대적 기하공차(GD&T)를 한 번에 검증합니다.");

    public override string FormatParameterSummary(InspectionSuiteParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        GeometricTolerancingOptions opt = parameters.GeometricTolerancing;
        return $"GD&T 직교도 공차=±{opt.AngleToleranceDeg:F1}° · 동심도 한계 Δc≤{opt.ConcentricityLimitPx:F2}px · 거리 공차=±{opt.DistanceTolerancePx:F1}px · 에지 대비≥{opt.GradientThreshold:F1}";
    }

    public override ImmutableArray<ImmutableArray<PointD>> BuildGeometricGuides(
        InspectionSuiteRoiInput target,
        InspectionSuiteParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(parameters);
        return BuildStandardFramedScanGuides(target);
    }

    public override ImmutableArray<InspectionSuiteItemResult> Execute(RoiInspectionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        GeometricTolerancingOptions raw = context.Parameters.GeometricTolerancing;
        return ExecuteEach(context, CropMarginPixels, raw, EvaluateTarget);
    }

    private static InspectionSuiteItemResult EvaluateTarget(
        RoiInspectionContext context,
        InspectionSuiteRoiInput target,
        LocalRoiCrop localCrop,
        GeometricTolerancingOptions rawOptions,
        Func<double> measureElapsedMicroseconds)
    {
        double minGrad = Math.Clamp((double)rawOptions.GradientThreshold, GeometricTolerancingOptions.MinGradientThreshold, GeometricTolerancingOptions.MaxGradientThreshold);
        double angleTol = Math.Clamp((double)rawOptions.AngleToleranceDeg, GeometricTolerancingOptions.MinAngleToleranceDeg, GeometricTolerancingOptions.MaxAngleToleranceDeg);
        double concLimit = Math.Clamp((double)rawOptions.ConcentricityLimitPx, GeometricTolerancingOptions.MinConcentricityLimitPx, GeometricTolerancingOptions.MaxConcentricityLimitPx);
        double distTol = Math.Clamp((double)rawOptions.DistanceTolerancePx, GeometricTolerancingOptions.MinDistanceTolerancePx, GeometricTolerancingOptions.MaxDistanceTolerancePx);

        GeometricToleranceResult gdt = GeometricToleranceAnalyzer.AnalyzeGeometricTolerances(
            localCrop.Crop.Pixels.Span,
            localCrop.Crop.Width,
            localCrop.Crop.Height,
            localCrop.Crop.Stride,
            nominalAngleDeg: NominalPerpendicularAngleDeg,
            angleToleranceDeg: angleTol,
            nominalCenterDistancePx: NominalCenterDistancePx,
            distanceTolerancePx: distTol,
            concentricityLimitPx: concLimit,
            gradientThreshold: minGrad);

        double elapsedUs = measureElapsedMicroseconds();
        var builder = new InspectionItemResultBuilder(target);

        if (gdt.IsMeasured)
        {
            var cornerPt = new PointD(localCrop.OffsetX + gdt.DatumIntersectionX, localCrop.OffsetY + gdt.DatumIntersectionY);
            var outerCenter = new PointD(localCrop.OffsetX + gdt.OuterCircleCenterX, localCrop.OffsetY + gdt.OuterCircleCenterY);
            var innerCenter = new PointD(localCrop.OffsetX + gdt.InnerCircleCenterX, localCrop.OffsetY + gdt.InnerCircleCenterY);

            builder.AddPrimaryPoint(cornerPt);
            builder.AddPrimaryPoint(outerCenter);
            builder.AddSecondaryPoint(innerCenter);

            builder.AddContour(BuildCirclePolygon(outerCenter, gdt.OuterCircleRadiusPx, OuterCirclePolygonSegments));
            builder.AddContour(BuildCirclePolygon(innerCenter, gdt.InnerCircleRadiusPx, InnerCirclePolygonSegments));
            builder.AddContour([cornerPt, outerCenter]);
        }

        bool isPass = gdt.IsMeasured && gdt.IsAllWithinTolerance;
        string badge = gdt.IsMeasured
            ? $"직교={gdt.LineToLineAngleDeg:F2}° · 동심도={gdt.CircleConcentricityOffsetPx:F3}px · 수직거리={gdt.BoreToDatumDistancePx:F2}px"
            : "GD&T 데이텀/보어 미검출";
        string detail = gdt.IsMeasured
            ? $"GD&T 기하공차 계측: 데이텀 직교각={gdt.LineToLineAngleDeg:F3}° (편차={gdt.AngleErrorDeg:F3}°), 내/외경 동심도 편차 Δc={gdt.CircleConcentricityOffsetPx:F3}px (외경 R={gdt.OuterCircleRadiusPx:F2}px, 내경 R={gdt.InnerCircleRadiusPx:F2}px), 보어-수평데이텀 수직거리={gdt.BoreToDatumDistancePx:F2}px, 수직데이텀 거리={gdt.LineToLinePerpendicularDistancePx:F2}px"
            : "유효한 직교 데이텀 에지 및 동심 원형 보어를 충분히 검출하지 못했습니다.";

        return builder.Build(isPass, badge, detail, elapsedUs);
    }
}
