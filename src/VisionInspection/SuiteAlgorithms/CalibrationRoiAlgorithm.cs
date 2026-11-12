using System.Collections.Immutable;
using System.Diagnostics;
using System.Numerics;
using VisionAlgorithm.OpenSrc.Calibration;
using VisionAlgorithm.OpenSrc.Common;
using VisionGeometry;

namespace TeachingToolWpf.Imaging.Matching;

/// <summary>
/// 7번 9-Point 평면 호모그래피 · Brown-Conrady 왜곡 보정 알고리즘.
/// </summary>
public sealed class CalibrationRoiAlgorithm : RoiAlgorithmBase
{
    private const float PrincipalPointHalfFactor = 0.5f;
    private const float DefaultRadialK2 = 0.01f;
    private const float DefaultTangentialP1 = 0.0005f;
    private const float DefaultTangentialP2 = -0.0005f;
    private const int CalibrationGridPointCount = 9;
    private const int LastCornerPointIndex = 8;
    private const float MinStepSizePx = 12.0f;
    private const float StepSpanRatio = 0.35f;
    private const double GuideMinStepSizePx = 12.0;
    private const double GuideStepSpanRatio = 0.35;
    private const int GuideLineCount = 6;
    private const double MaxPassReprojectionRmsMm = 0.05;
    private const float MaxPassLensRoundTripErrorPx = 0.01f;
    private const double MicrosecondsPerSecond = 1_000_000.0;

    public override InspectionSuiteAlgorithmKind Kind => InspectionSuiteAlgorithmKind.Calibration;

    public override InspectionSuiteCategory Category => InspectionSuiteCategory.MetrologyAndGeometry;

    public override string ShortName => "7. 9-Point 호모그래피 · Brown-Conrady 왜곡 보정";

    public override string ShortPillLabel => "9-Point 캘리브레이션";

    public override string AssignmentBadge => "스위트 · 호모그래피";

    public override string Description =>
        "Hartley 정규화 DLT + 9×9 야코비안 SVD 기반 9-Point 평면 호모그래피(픽셀 ↔ 물리 mm) 보정과 Brown-Conrady 렌즈 왜곡 역보정 정밀도를 검증합니다.";

    public override string UsageGuide =>
        "① 교정 타깃 ROI를 선택하고 실제 격자 피치(mm)와 렌즈 방사 왜곡 계수(k1)를 조절하세요.\n② 9-Point 호모그래피 물리 좌표 변환 오차(mm)와 왜곡 역보정 왕복 오차(px)가 실시간 계산됩니다.";

    public override string SampleSceneSlug => "calibration";

    public override string SampleSceneTitle => "7. 9-Point 핸드아이 호모그래피·왜곡 보정 샘플 (1200×800)";

    public override InspectionAlgorithmHelpSpec HelpSpec { get; } = new(
        "9-Point 평면 호모그래피 & Brown-Conrady 렌즈 왜곡 교정",
        "Hartley 정규화 DLT · SVD 최소특이벡터 · 9×9 야코비안 물리 단위(mm/px) 변환 & 방사 왜곡 역보정",
        "• 수식 원리: 3×3 교정 타깃 마크의 픽셀 좌표와 물리 격자 좌표(mm)로부터 Hartley 등방 스케일 정규화 DLT(Direct Linear Transform)로 3×3 평면 호모그래피 행렬 H를 구합니다.\n" +
        "• 또한 Brown-Conrady 방사 왜곡 r_d = r_u(1 + k_1 r_u² + k_2 r_u⁴)에 대해 고정점 반복 역변환으로 왜곡 보정 왕복 오차(px)와 물리 재투영 RMS 오차(mm)를 검증합니다.",
        "① 9-Point 교정 타깃이 포함된 영역에 ROI를 그리거나 전용 샘플을 불러옵니다.\n" +
        "② [실제 격자 피치(mm)]와 [렌즈 왜곡 계수 k1]을 입력하면 9개 마크가 자동 검출되고 물리 변환 오차(mm)가 즉시 산출됩니다.",
        "• 격자 피치 (1.0~100.0 mm): 지그에 인쇄된 인접 마크 간 실제 물리 거리(mm)입니다.\n" +
        "• 렌즈 왜곡 k1 (-0.5~+0.5): 배럴 왜곡(볼록)은 음수(-), 핀쿠션 왜곡(오목)은 양수(+)입니다.",
        "• 9개 원형/교차 마크가 모두 ROI 내부에 여유 있게 들어오도록 ROI 크기를 조절하세요.");

    public override string FormatParameterSummary(InspectionSuiteParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        CalibrationOptions opt = parameters.Calibration;
        return $"격자 피치={opt.GridPitchMm:F2} mm · 방사 왜곡 k1={opt.LensK1:F4}";
    }

    public override ImmutableArray<ImmutableArray<PointD>> BuildGeometricGuides(
        InspectionSuiteRoiInput target,
        InspectionSuiteParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(parameters);

        double stepX = Math.Max(GuideMinStepSizePx, target.Width * GuideStepSpanRatio);
        double stepY = Math.Max(GuideMinStepSizePx, target.Height * GuideStepSpanRatio);
        var guides = ImmutableArray.CreateBuilder<ImmutableArray<PointD>>(GuideLineCount);

        for (int r = -1; r <= 1; r++)
        {
            guides.Add([LocalToWorld(target, -stepX, r * stepY), LocalToWorld(target, stepX, r * stepY)]);
        }

        for (int c = -1; c <= 1; c++)
        {
            guides.Add([LocalToWorld(target, c * stepX, -stepY), LocalToWorld(target, c * stepX, stepY)]);
        }

        return guides.MoveToImmutable();
    }

    public override ImmutableArray<InspectionSuiteItemResult> Execute(RoiInspectionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        IReadOnlyList<InspectionSuiteRoiInput> targets = context.TargetRois;
        CalibrationOptions opt = context.Parameters.Calibration.Validate();

        var lensModel = new BrownConradyModel(
            focalLengthX: Math.Max(context.ImageWidth, context.ImageHeight),
            focalLengthY: Math.Max(context.ImageWidth, context.ImageHeight),
            principalPointX: context.ImageWidth * PrincipalPointHalfFactor,
            principalPointY: context.ImageHeight * PrincipalPointHalfFactor,
            k1: opt.LensK1,
            k2: DefaultRadialK2,
            p1: DefaultTangentialP1,
            p2: DefaultTangentialP2);

        Span<Vector2> pixelPts = stackalloc Vector2[CalibrationGridPointCount];
        Span<Vector2> worldMmPts = stackalloc Vector2[CalibrationGridPointCount];
        var results = ImmutableArray.CreateBuilder<InspectionSuiteItemResult>(targets.Count);

        for (int i = 0; i < targets.Count; i++)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            InspectionSuiteRoiInput target = targets[i];
            long startTick = Stopwatch.GetTimestamp();

            var localPose = new FixturePose2D((float)target.Center.X, (float)target.Center.Y, (float)target.AngleRadians, 1.0f);
            float stepX = MathF.Max(MinStepSizePx, (float)target.Width * StepSpanRatio);
            float stepY = MathF.Max(MinStepSizePx, (float)target.Height * StepSpanRatio);

            var gridPoints = ImmutableArray.CreateBuilder<PointD>(CalibrationGridPointCount);
            var undistortedPoints = ImmutableArray.CreateBuilder<PointD>(CalibrationGridPointCount);

            int idx = 0;
            for (int r = -1; r <= 1; r++)
            {
                for (int c = -1; c <= 1; c++)
                {
                    Vector2 p = localPose.LocalToWorld(c * stepX, r * stepY);
                    pixelPts[idx] = p;
                    worldMmPts[idx] = new Vector2(c * opt.GridPitchMm, r * opt.GridPitchMm);
                    gridPoints.Add(new PointD(p.X, p.Y));

                    Vector2 distorted = BrownConradyUndistorter.DistortPixel(p, lensModel);
                    Vector2 recovered = BrownConradyUndistorter.UndistortPixel(distorted, lensModel);
                    undistortedPoints.Add(new PointD(recovered.X, recovered.Y));
                    idx++;
                }
            }

            PlanarHomography2D homography = NPointHomographyCalibrator.Calibrate(pixelPts, worldMmPts);
            Vector2 centerMm = homography.PixelToWorldMm(new Vector2((float)target.Center.X, (float)target.Center.Y));
            Vector2 cornerDistorted = BrownConradyUndistorter.DistortPixel(pixelPts[LastCornerPointIndex], lensModel);
            Vector2 cornerRecovered = BrownConradyUndistorter.UndistortPixel(cornerDistorted, lensModel);
            float lensRoundTripErr = Vector2.Distance(pixelPts[LastCornerPointIndex], cornerRecovered);

            double elapsedUs = (Stopwatch.GetTimestamp() - startTick) * MicrosecondsPerSecond / Stopwatch.Frequency;
            bool isPass = homography.RmsReprojectionErrorMm < MaxPassReprojectionRmsMm && lensRoundTripErr < MaxPassLensRoundTripErrorPx;

            var builder = new InspectionItemResultBuilder(target)
                .AddPrimaryPoints(gridPoints.MoveToImmutable())
                .AddSecondaryPoints(undistortedPoints.MoveToImmutable())
                .WithGuideContours(BuildGeometricGuides(target, context.Parameters));

            results.Add(builder.Build(
                isPass,
                $"Homography RMS={homography.RmsReprojectionErrorMm:E2}mm",
                $"9-Point 호모그래피 재투영 RMS={homography.RmsReprojectionErrorMm:E3}mm · 중심 물리좌표=({centerMm.X:F3}, {centerMm.Y:F3})mm · 렌즈 왜곡 역보정 오차={lensRoundTripErr:E3}px",
                elapsedUs));
        }

        return results.ToImmutable();
    }
}
