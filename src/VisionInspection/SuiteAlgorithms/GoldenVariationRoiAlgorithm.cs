using System.Collections.Immutable;
using System.Numerics;
using VisionAlgorithm;
using VisionAlgorithm.OpenSrc.Common;
using VisionAlgorithm.OpenSrc.GoldenVariation;
using VisionGeometry;

namespace TeachingToolWpf.Imaging.Matching;

/// <summary>
/// 2번 통계적 골든 마스터 변이 검사 (Welford + Sobel) 알고리즘.
/// </summary>
public sealed class GoldenVariationRoiAlgorithm : RoiAlgorithmBase
{
    private const int CropMarginPixels = 16;
    private const int MinModelRoiDimensionPx = 16;
    private const int MaxModelRoiDimensionPx = 512;
    private const float SingleSampleJitterDxPx = 0.25f;
    private const float SingleSampleJitterDyPx = 0.20f;
    private const float AbsoluteMinimumThresholdGv = 10.0f;
    private const float MinWarpScale = 0.25f;
    private const float MaxWarpScale = 4.0f;
    private const int MaxPlottedDefectDensityDivisor = 120;

    private readonly record struct TrainedVariationState(
        GoldenVariationOptions Options,
        VariationModel Model,
        int RoiWidth,
        int RoiHeight,
        byte[] MaskBuffer);

    public override InspectionSuiteAlgorithmKind Kind => InspectionSuiteAlgorithmKind.GoldenVariation;

    public override InspectionSuiteCategory Category => InspectionSuiteCategory.SurfaceBlobAnd3D;

    public override string ShortName => "2. 통계적 골든 마스터 변이 검사 (Welford + Sobel)";

    public override string ShortPillLabel => "골든 마스터 변이";

    public override string AssignmentBadge => "스위트 · 골든변이";

    public override bool UsesReferenceRois => true;

    public override string Description =>
        "정상 ROI 샘플들의 Welford 평균(Mean)·표준편차(StdDev)와 Sobel 에지 기울기 허용치(k·σ + c·|∇μ|)를 결합해 실제 결함 픽셀만 검출합니다.";

    public override string UsageGuide =>
        "① 정상(Golden) ROI들을 선택해 '정상(Good) 기준 등록'을 누르세요 (샘플 열기 시 상단 4개 다이가 자동 등록됩니다).\n② 표준편차 배수(k)와 에지 지터 허용치(c) 슬라이더를 조절하면 하단 검사 다이의 스크래치·솔더브릿지 불량 픽셀(빨강 점)이 실시간 표시됩니다.";

    public override string SampleSceneSlug => "golden";

    public override string SampleSceneTitle => "2. 통계적 골든 마스터 변이 검사 합성 샘플 (1200×800)";

    public override InspectionAlgorithmHelpSpec HelpSpec { get; } = new(
        "골든 다이 변이 모델 검사 (HALCON variation_model 급 통계적 외관 비교)",
        "Welford 온라인 평균(μ)·표준편차(σ) 학습 · Sobel 에지 지터 허용 대역 T(x) = k·σ(x) + c·‖∇μ(x)‖",
        "• 수식 원리: 복수의 양품(Golden) ROI로부터 픽셀별 평균 영상 μ(x,y)와 표준편차 σ(x,y), 그리고 평균 영상의 Sobel 기울기 크기 ‖∇μ(x,y)‖를 계산합니다.\n" +
        "• 검사 시 단순 차영상 |I - μ| > T 대신, 미세 위치 공차(Edge Jitter)가 집중되는 경계부에서 임계치를 자동으로 넓혀 주는 적응형 문턱값 |I(x,y) - μ(x,y)| > max(MinContrast, k·σ(x,y) + c·‖∇μ(x,y)‖)를 적용해 경계 가짜 불량을 제거합니다.",
        "① 양품 다이/패드 ROI들을 선택하고 [정상(Good) 등록]을 눌러 골든 통계 모델을 구성합니다(샘플 로드 시 상단 4개 양품 자동 등록).\n" +
        "② [전체 ROI 실행] 또는 실시간 프리뷰로 하단 검사 대상 ROI의 결함 픽셀(빨강 점)과 면적을 확인합니다.",
        "• 표준편차 배수 k (1.0~8.0, 기본 3.0): 양품 표면의 자연스러운 밝기 변동을 몇 σ까지 허용할지 결정합니다.\n" +
        "• 에지 지터 허용치 c (0.0~2.0, 기본 0.5): 부품 미세 흔들림(±1px)으로 인한 외곽선 오검출이 뜨면 c를 0.6~1.0으로 올리세요.\n" +
        "• 최소 불량 면적 (1~500 px): 미세 먼지를 무시하려면 최소 불량 면적을 8~20 px로 설정하세요.",
        "• 정상 등록 ROI와 검사 대상 ROI의 가로·세로 크기는 내부적으로 골든 그리드에 맞춰 정규화 샘플링됩니다.");

    public override string FormatParameterSummary(InspectionSuiteParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        GoldenVariationOptions opt = parameters.GoldenVariation;
        return $"표준편차 배수 k={opt.VariationStdMultiplier:F1} · 에지 지터 허용 c={opt.VariationEdgeMultiplier:F2} · 최소 결함면적={opt.MinimumDefectAreaPixels}px";
    }

    public override ImmutableArray<InspectionSuiteItemResult> Execute(RoiInspectionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        GoldenVariationOptions opt = context.Parameters.GoldenVariation.Validate();
        IReadOnlyList<InspectionSuiteRoiInput> trainRois = context.ReferenceRois.Count > 0
            ? context.ReferenceRois
            : [context.TargetRois[0]];
        int roiWidth = Math.Clamp((int)Math.Round(trainRois[0].Width), MinModelRoiDimensionPx, MaxModelRoiDimensionPx);
        int roiHeight = Math.Clamp((int)Math.Round(trainRois[0].Height), MinModelRoiDimensionPx, MaxModelRoiDimensionPx);

        var trainSamples = new List<VariationTrainingSample>();
        var cropBuffers = new List<(ShapeGrayImage Crop, FixturePose2D LocalPose)>(trainRois.Count);

        for (int i = 0; i < trainRois.Count; i++)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            InspectionSuiteRoiInput refRoi = trainRois[i];
            LocalRoiCrop refCrop = ReadLocalCrop(context.Reader, context.ImageWidth, context.ImageHeight, refRoi, CropMarginPixels, context.CancellationToken);
            var basePose = new FixturePose2D(refCrop.LocalCenter.X, refCrop.LocalCenter.Y, (float)refRoi.AngleRadians, 1.0f);
            cropBuffers.Add((refCrop.Crop, basePose));
        }

        // [Math Why]: 기준 ROI가 1장뿐일 때 Welford 분산이 0으로 퇴화하지 않도록 ±0.25px 서브픽셀 지터 샘플을 추가한다.
        for (int i = 0; i < cropBuffers.Count; i++)
        {
            var (crop, pose) = cropBuffers[i];
            trainSamples.Add(new VariationTrainingSample(crop.Pixels.ToArray(), crop.Width, crop.Height, crop.Stride, pose));
            if (cropBuffers.Count == 1)
            {
                trainSamples.Add(new VariationTrainingSample(
                    crop.Pixels.ToArray(),
                    crop.Width,
                    crop.Height,
                    crop.Stride,
                    new FixturePose2D(pose.CenterX + SingleSampleJitterDxPx, pose.CenterY - SingleSampleJitterDyPx, pose.AngleRad, 1.0f)));
                trainSamples.Add(new VariationTrainingSample(
                    crop.Pixels.ToArray(),
                    crop.Width,
                    crop.Height,
                    crop.Stride,
                    new FixturePose2D(pose.CenterX - SingleSampleJitterDxPx, pose.CenterY + SingleSampleJitterDyPx, pose.AngleRad, 1.0f)));
            }
        }

        var spec = new VariationTrainingSpec(
            absoluteMinimumThreshold: AbsoluteMinimumThresholdGv,
            sigmaMultiplier: opt.VariationStdMultiplier,
            edgeJitterTolerancePixels: opt.VariationEdgeMultiplier);

        VariationModel model = VariationModelTrainer.Train(trainSamples.ToArray(), roiWidth, roiHeight, spec, context.Workspace);
        byte[] maskBuf = new byte[roiWidth * roiHeight];
        var state = new TrainedVariationState(opt, model, roiWidth, roiHeight, maskBuf);

        return ExecuteEach(context, CropMarginPixels, state, EvaluateTarget);
    }

    private static InspectionSuiteItemResult EvaluateTarget(
        RoiInspectionContext context,
        InspectionSuiteRoiInput target,
        LocalRoiCrop localCrop,
        TrainedVariationState state,
        Func<double> measureElapsedMicroseconds)
    {
        var (crop, localCenter, offsetX, offsetY) = localCrop;
        float scale = (float)Math.Sqrt((target.Width * target.Height) / ((double)state.RoiWidth * state.RoiHeight));
        var localPose = new FixturePose2D(localCenter.X, localCenter.Y, (float)target.AngleRadians, Math.Clamp(scale, MinWarpScale, MaxWarpScale));

        VariationInspectionResult res = VariationModelInspector.Inspect(
            crop.Pixels.Span,
            crop.Width,
            crop.Height,
            crop.Stride,
            localPose,
            state.Model,
            minimumDefectAreaPixels: state.Options.MinimumDefectAreaPixels,
            workspace: context.Workspace,
            binaryDefectMaskOut: state.MaskBuffer);

        var builder = new InspectionItemResultBuilder(target);
        int step = Math.Max(1, (int)Math.Sqrt(Math.Max(1, res.DefectPixelCount / MaxPlottedDefectDensityDivisor)));
        for (int y = 0; y < state.RoiHeight; y += step)
        {
            for (int x = 0; x < state.RoiWidth; x += step)
            {
                if (state.MaskBuffer[(y * state.RoiWidth) + x] != 0)
                {
                    float lx = (x + 0.5f) - (state.RoiWidth * 0.5f);
                    float ly = (y + 0.5f) - (state.RoiHeight * 0.5f);
                    Vector2 wp = localPose.LocalToWorld(lx, ly);
                    builder.AddDefectPoint(new PointD(wp.X + offsetX, wp.Y + offsetY));
                }
            }
        }

        double elapsedUs = measureElapsedMicroseconds();
        bool isPass = res.IsDefectFree;
        float defectRatio = (float)res.DefectPixelCount / Math.Max(1, state.RoiWidth * state.RoiHeight);
        string statusTag = isPass ? "정상(GOOD)" : "변이불량(NG)";
        string badge = $"{statusTag} · 초과={res.DefectPixelCount}px";
        string detail = $"{statusTag} · 초과 픽셀={res.DefectPixelCount}개 ({defectRatio * 100f:F2}%) · 최대 초과 편차={res.MaxExcessDifference:F1} GV (Z={res.MaxNormalizedZScore:F2})";

        return builder.Build(isPass, badge, detail, elapsedUs);
    }
}
