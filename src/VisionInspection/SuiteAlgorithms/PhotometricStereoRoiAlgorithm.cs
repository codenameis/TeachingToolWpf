using System.Collections.Immutable;
using VisionAlgorithm.OpenSrc.PhotometricStereo;
using VisionGeometry;

namespace TeachingToolWpf.Imaging.Matching;

/// <summary>
/// 19번 Woodham 4방향 포토메트릭 스테레오 법선·알베도 분리 및 3D 곡률 결함 검사 알고리즘.
/// </summary>
public sealed class PhotometricStereoRoiAlgorithm : RoiAlgorithmBase
{
    private const int CropMarginPixels = 0;

    public override InspectionSuiteAlgorithmKind Kind => InspectionSuiteAlgorithmKind.PhotometricStereo;

    public override InspectionSuiteCategory Category => InspectionSuiteCategory.SurfaceBlobAnd3D;

    public override string ShortName => "19. [본검사] 4방향 포토메트릭 스테레오 법선·알베도·3D 평균/가우시안 곡률 결함 검사";

    public override string ShortPillLabel => "포토메트릭 3D 표면";

    public override string AssignmentBadge => "스위트 · 3D포토메트릭";

    public override string Description =>
        "Woodham 4방향 람베르시안 포토메트릭 스테레오 법선 벡터(nx,ny,nz) 및 순수 알베도(ρ) 분리, 3D 평균곡률(H)·가우시안곡률(K) 기반 미세 찍힘/스크래치 결함 검사를 수행합니다.";

    public override string UsageGuide =>
        "① 4방향 조명 패치(또는 단일 프레임 ROI)를 선택하세요.\n② 2D 얼룩(Albedo)을 완벽히 배제하고 3D 표면 법선 곡률(|H|, |K|)이 집중된 찍힘·스크래치 결함만 검출합니다.";

    public override string SampleSceneSlug => "photometric";

    public override string SampleSceneTitle => "19. [본검사] 4방향 포토메트릭 스테레오 법선·알베도·3D 곡률 결함 샘플 (1200×800)";

    public override InspectionAlgorithmHelpSpec HelpSpec { get; } = new(
        "[3D 표면검사] 4방향 포토메트릭 스테레오(Woodham) 법선 벡터 · 알베도 · 평균/가우시안 곡률 결함 검사",
        "4방향 링 조명(동·북·서·남) 람베르시안 역산 · 2D 얼룩(Albedo) 완벽 배제 · 3D 평균곡률(H) & 가우시안곡률(K)",
        "• 수식 원리:\n" +
        "  1) Woodham 4-Light Lambertian 해법: 경사각 σ, 방위각 0°(동), 90°(북), 180°(서), 270°(남) 4장의 조명 영상(또는 4분할 멀티샷 프레임)으로부터 g_x = (I_E - I_W)/(2 sin σ), g_y = (I_S - I_N)/(2 sin σ), g_z = (I_E + I_N + I_W + I_S)/(4 cos σ)를 구해 순수 2D 표면 반사율(Albedo ρ = ‖g‖)과 3D 단위 법선 벡터 n = g / ρ를 닫힌 해로 완벽히 분리합니다.\n" +
        "  2) 미분 기하 곡률(Curvature): 표면 기울기 p = -n_x/n_z, q = -n_y/n_z의 편미분으로 평균 곡률 H = 0.5·(∂p/∂x + ∂q/∂y)와 가우시안 곡률 K = (∂p/∂x)(∂q/∂y) - (∂p/∂y)²를 계산합니다.",
        "① 4분할 포토메트릭 스테레오 패치 위에 ROI를 배치합니다 (좌측 패치: 2D 검은 얼룩만 있고 3D 평탄한 정상 표면 / 우측 패치: 2D 얼룩 + 3D 미세 찍힘 Dent 결함).\n" +
        "② [선택 ROI 실행]을 누르면 2D 얼룩은 무시하고 실제 3D 찍힘/스크래치 요철 결함만 정확히 검출합니다.",
        "• 최소 에지 대비 슬라이더: 3D 평균 곡률(|H|) 결함 검출 문턱값으로 연동됩니다.\n" +
        "• 골든 최소 결함 면적 (px): 허용 가능한 미세 곡률 픽셀 수 한계입니다.",
        "• 일반 2D 카메라 검사에서 단순 유분 얼룩·산화 변색 때문에 발생하는 가성 불량(Overkill)을 원천 해결하고 진짜 3D 찍힘(Dent)·찍힌 자국·양각 각인만 골라냅니다!");

    public override string FormatParameterSummary(InspectionSuiteParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        PhotometricStereoOptions opt = parameters.PhotometricStereo;
        return $"Woodham 조명 고도각={opt.SlantAngleDeg:F0}° · 3D 곡률 임계(|H|)={opt.CurvatureThreshold:F3} · 결함 화소 한계={opt.MaxDefectPixels}px";
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
        PhotometricStereoOptions raw = context.Parameters.PhotometricStereo;
        return ExecuteEach(context, CropMarginPixels, raw, EvaluateTarget);
    }

    private static InspectionSuiteItemResult EvaluateTarget(
        RoiInspectionContext context,
        InspectionSuiteRoiInput target,
        LocalRoiCrop localCrop,
        PhotometricStereoOptions rawOptions,
        Func<double> measureElapsedMicroseconds)
    {
        double slantDeg = Math.Clamp((double)rawOptions.SlantAngleDeg, PhotometricStereoOptions.MinSlantAngleDeg, PhotometricStereoOptions.MaxSlantAngleDeg);
        double curvThreshold = Math.Clamp((double)rawOptions.CurvatureThreshold, PhotometricStereoOptions.MinCurvatureThreshold, PhotometricStereoOptions.MaxCurvatureThreshold);
        int maxDefectPixels = Math.Clamp(rawOptions.MaxDefectPixels, PhotometricStereoOptions.MinMaxDefectPixels, PhotometricStereoOptions.MaxMaxDefectPixels);

        PhotometricStereoResult ps = PhotometricStereoEngine.AnalyzeCompositeOrSingleFrame(
            localCrop.Crop.Pixels.Span,
            localCrop.Crop.Width,
            localCrop.Crop.Height,
            localCrop.Crop.Stride,
            slantAngleDeg: slantDeg,
            curvatureThreshold: curvThreshold);

        double elapsedUs = measureElapsedMicroseconds();
        var builder = new InspectionItemResultBuilder(target);

        if (ps.DefectPixelCount > 0)
        {
            var peakWorld = new PointD(localCrop.OffsetX + ps.PeakDefectX, localCrop.OffsetY + ps.PeakDefectY);
            builder.AddDefectPoint(peakWorld);
            builder.AddPrimaryPoint(peakWorld);
        }
        else
        {
            builder.AddPrimaryPoint(target.Center);
        }

        bool isPass = ps.DefectPixelCount <= maxDefectPixels;
        string statusTag = isPass ? "3D표면정상(OK)" : "3D요철결함(NG)";
        string badge = $"{statusTag} · |H|max={ps.MaxAbsMeanCurvature:F3} · 결함={ps.DefectPixelCount}/{maxDefectPixels}px";
        string detail = $"4방향 포토메트릭 스테레오(Slant={slantDeg:F0}°, |H|임계={curvThreshold:F3}): {statusTag}, 평균 알베도={ps.MeanAlbedo:F1}GV, 최대 평균곡률 |H|={ps.MaxAbsMeanCurvature:F4}, 가우시안곡률 |K|={ps.MaxAbsGaussianCurvature:F4}, 3D 요철 결함 픽셀={ps.DefectPixelCount}개 (영역={ps.DefectRegionCount}개)";

        return builder.Build(isPass, badge, detail, elapsedUs);
    }
}
