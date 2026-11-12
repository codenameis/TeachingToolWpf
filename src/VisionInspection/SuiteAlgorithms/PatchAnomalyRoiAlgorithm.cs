using System.Collections.Immutable;
using System.Numerics;
using VisionAlgorithm.OpenSrc.Common;
using VisionAlgorithm.OpenSrc.GoldenVariation;
using VisionAlgorithm.OpenSrc.PatchAnomaly;
using VisionGeometry;

namespace TeachingToolWpf.Imaging.Matching;

/// <summary>
/// 6번 PaDiM 국소 패치 이상치 히트맵 탐지 알고리즘.
/// </summary>
public sealed class PatchAnomalyRoiAlgorithm : RoiAlgorithmBase
{
    private const int CropMarginPixels = 16;
    private const int MinPatchMultiplier = 2;
    private const double MinTrainRoiDimensionPx = 32.0;
    private const double MaxTrainRoiDimensionPx = 256.0;
    private const float MinAffineScale = 0.25f;
    private const float MaxAffineScale = 4.0f;
    private const int MinRawSamplesWithoutJitter = 4;
    private const float PrimarySubpixelJitter = 0.35f;
    private const float SecondarySubpixelJitter = 0.25f;
    private const float BaselineLambda = 0.01f;
    private const float LambdaThresholdSlope = 2.0f;
    private const float MinThresholdScale = 0.5f;
    private const float MaxThresholdScale = 3.0f;
    private const float HalfCenterFactor = 0.5f;

    private readonly record struct TrainedPadimState(
        PadimHeatmapDetector Detector,
        float[] HeatmapBuffer,
        int RoiWidth,
        int RoiHeight,
        float RegularizationLambda);

    public override InspectionSuiteAlgorithmKind Kind => InspectionSuiteAlgorithmKind.PatchAnomaly;

    public override InspectionSuiteCategory Category => InspectionSuiteCategory.SurfaceBlobAnd3D;

    public override string ShortName => "6. PaDiM 패치 이상치 히트맵 탐지";

    public override string ShortPillLabel => "PaDiM 패치 이상";

    public override string AssignmentBadge => "스위트 · PaDiM";

    public override string Description =>
        "PaDiM 방식으로 정상 ROI의 공간 패치(Grid)별 다변량 가우시안 분포를 학습하고 패치별 마할라노비스 거리 히트맵과 최대 이상치 좌표를 검출합니다.";

    public override string UsageGuide =>
        "① 정상 ROI들을 '정상 기준 등록'으로 등록한 뒤 패치 크기(px)를 설정하세요.\n② 타깃 ROI 내 국소 볼 누락·이물 등 최대 이상치 피크 위치(빨강/청록 점)와 마할라노비스 이상 점수를 산출합니다.";

    public override string SampleSceneSlug => "padim";

    public override string SampleSceneTitle => "6. PaDiM 공간 패치 이상 히트맵 합성 샘플 (1200×800)";

    public override bool UsesReferenceRois => true;

    public override InspectionAlgorithmHelpSpec HelpSpec { get; } = new(
        "PaDiM 국소 패치 가우시안 이상 탐지 (Patch Distribution Modeling)",
        "공간 격자(Grid)별 다변량 가우시안 N(μ_{i,j}, Σ_{i,j} + λI) · 국소 마할라노비스 이상치 피크 맵",
        "• 수식 원리: 정상 ROI들을 격자 패치(i,j)로 나누어 각 위치마다 고유한 특징 평균 μ_{i,j}와 공분산 Σ_{i,j} + λI를 학습합니다.\n" +
        "• 검사 시 각 공간 패치의 마할라노비스 거리를 계산하여 전체 평균에 묻히기 쉬운 국소 미세 결함(BGA 볼 누락, 국소 찍힘, 이물)의 정확한 좌표와 이상 점수를 산출합니다.",
        "① 정상 부품 ROI들을 선택해 [정상(Good) 등록]으로 등록합니다.\n" +
        "② [패치 크기(px)]와 [공분산 정규화 λ]를 설정한 뒤 검사하면 가장 이상도가 높은 결함 위치에 마커가 표시됩니다.",
        "• 패치 크기 (4~32 px): 미세 결함은 6~8 px, 넓은 얼룩은 12~16 px가 적합합니다.\n" +
        "• 정규화 계수 λ (0.001~0.5): 위치 정렬 오차에 둔감하게 만들려면 λ를 0.02~0.08로 높이세요.",
        "• 부품 전체가 많이 흔들리는 공정에서는 먼저 코너 정합이나 패턴 매칭으로 위치를 보정한 뒤 PaDiM을 적용하세요.");

    public override string FormatParameterSummary(InspectionSuiteParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        PatchAnomalyOptions opt = parameters.PatchAnomaly;
        return $"PaDiM 패치 크기={opt.PatchSize}×{opt.PatchSize} px · 공분산 정규화 λ={opt.RegularizationLambda:F3}";
    }

    public override ImmutableArray<InspectionSuiteItemResult> Execute(RoiInspectionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        IReadOnlyList<InspectionSuiteRoiInput> targets = context.TargetRois;
        IReadOnlyList<InspectionSuiteRoiInput> references = context.ReferenceRois;
        PatchAnomalyOptions opt = context.Parameters.PatchAnomaly.Validate();

        IReadOnlyList<InspectionSuiteRoiInput> trainRois = references.Count > 0 ? references : [targets[0]];
        int patchSize = opt.PatchSize;
        int roiWidth = Math.Max(patchSize * MinPatchMultiplier, ((int)Math.Clamp(trainRois[0].Width, MinTrainRoiDimensionPx, MaxTrainRoiDimensionPx) / patchSize) * patchSize);
        int roiHeight = Math.Max(patchSize * MinPatchMultiplier, ((int)Math.Clamp(trainRois[0].Height, MinTrainRoiDimensionPx, MaxTrainRoiDimensionPx) / patchSize) * patchSize);

        var normalPatches = new List<float[]>(trainRois.Count * MinRawSamplesWithoutJitter);
        ReadOnlySpan<(float Dx, float Dy)> patchOffsets =
        [
            (-PrimarySubpixelJitter, -SecondarySubpixelJitter),
            (PrimarySubpixelJitter, SecondarySubpixelJitter),
            (-SecondarySubpixelJitter, PrimarySubpixelJitter)
        ];

        for (int i = 0; i < trainRois.Count; i++)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            InspectionSuiteRoiInput refRoi = trainRois[i];
            LocalRoiCrop refCrop = ReadLocalCrop(context.Reader, context.ImageWidth, context.ImageHeight, refRoi, CropMarginPixels, context.CancellationToken);
            float scale = (float)Math.Sqrt((refRoi.Width * refRoi.Height) / ((double)roiWidth * roiHeight));
            var basePose = new FixturePose2D(refCrop.LocalCenter.X, refCrop.LocalCenter.Y, (float)refRoi.AngleRadians, Math.Clamp(scale, MinAffineScale, MaxAffineScale));

            float[] warped = new float[roiWidth * roiHeight];
            SimdAffineWarper.WarpRectifiedRoi(refCrop.Crop.Pixels.Span, refCrop.Crop.Width, refCrop.Crop.Height, refCrop.Crop.Stride, basePose, roiWidth, roiHeight, warped);
            normalPatches.Add(warped);

            if (trainRois.Count < MinRawSamplesWithoutJitter)
            {
                for (int j = 0; j < patchOffsets.Length; j++)
                {
                    (float dx, float dy) = patchOffsets[j];
                    var jitterPose = new FixturePose2D(basePose.CenterX + dx, basePose.CenterY + dy, basePose.AngleRad, basePose.Scale);
                    float[] jitterWarped = new float[roiWidth * roiHeight];
                    SimdAffineWarper.WarpRectifiedRoi(refCrop.Crop.Pixels.Span, refCrop.Crop.Width, refCrop.Crop.Height, refCrop.Crop.Stride, jitterPose, roiWidth, roiHeight, jitterWarped);
                    normalPatches.Add(jitterWarped);
                }
            }
        }

        PadimHeatmapDetector detector = PadimHeatmapDetector.Train(normalPatches, roiWidth, roiHeight, patchSize: patchSize);
        float[] heatmap = new float[roiWidth * roiHeight];
        var state = new TrainedPadimState(detector, heatmap, roiWidth, roiHeight, context.Parameters.PatchAnomaly.RegularizationLambda);
        return ExecuteEach(context, CropMarginPixels, state, EvaluateTarget);
    }

    private static InspectionSuiteItemResult EvaluateTarget(
        RoiInspectionContext context,
        InspectionSuiteRoiInput target,
        LocalRoiCrop localCrop,
        TrainedPadimState state,
        Func<double> measureElapsedMicroseconds)
    {
        var (crop, localCenter, offsetX, offsetY) = localCrop;
        float scale = (float)Math.Sqrt((target.Width * target.Height) / ((double)state.RoiWidth * state.RoiHeight));
        var localPose = new FixturePose2D(localCenter.X, localCenter.Y, (float)target.AngleRadians, Math.Clamp(scale, MinAffineScale, MaxAffineScale));

        Span<float> targetWarped = context.Workspace.GetFloatBufferA(state.RoiWidth * state.RoiHeight);
        SimdAffineWarper.WarpRectifiedRoi(crop.Pixels.Span, crop.Width, crop.Height, crop.Stride, localPose, state.RoiWidth, state.RoiHeight, targetWarped);

        PadimAnomalyResult res = state.Detector.Inspect(targetWarped, context.Workspace, state.HeatmapBuffer);
        float padimThresholdScale = Math.Clamp(1.0f + ((state.RegularizationLambda - BaselineLambda) * LambdaThresholdSlope), MinThresholdScale, MaxThresholdScale);
        float effectivePadimThreshold = state.Detector.AnomalyThreshold * padimThresholdScale;
        float peakLocalX = res.PeakAnomalyLocalPosition.X - (state.RoiWidth * HalfCenterFactor);
        float peakLocalY = res.PeakAnomalyLocalPosition.Y - (state.RoiHeight * HalfCenterFactor);
        Vector2 peakWorldInCrop = localPose.LocalToWorld(peakLocalX, peakLocalY);
        var peakWorld = new PointD(peakWorldInCrop.X + offsetX, peakWorldInCrop.Y + offsetY);

        double elapsedUs = measureElapsedMicroseconds();
        bool isPass = res.ImageAnomalyScore <= effectivePadimThreshold;
        string statusTag = isPass ? "PaDiM 정상(OK)" : "PaDiM 이상(ANOMALY)";

        var builder = new InspectionItemResultBuilder(target);
        if (isPass)
        {
            builder.AddPrimaryPoint(peakWorld);
        }
        else
        {
            builder.AddDefectPoint(peakWorld);
        }

        return builder.Build(
            isPass,
            $"{statusTag} · Max={res.ImageAnomalyScore:F2}",
            $"{statusTag} · 최대 패치 마할라노비스 점수={res.ImageAnomalyScore:F3} (임계치={effectivePadimThreshold:F3}, 초과픽셀={res.AnomalousPixelCount}) · 피크 좌표=({peakWorld.X:F1}, {peakWorld.Y:F1})",
            elapsedUs);
    }
}
