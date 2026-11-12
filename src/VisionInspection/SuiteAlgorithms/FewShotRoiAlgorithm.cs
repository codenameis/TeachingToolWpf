using System.Collections.Immutable;
using System.Diagnostics;
using VisionAlgorithm.OpenSrc.Common;
using VisionAlgorithm.OpenSrc.EdgeFewShot.Engine;
using VisionAlgorithm.OpenSrc.GoldenVariation;
using VisionGeometry;

namespace TeachingToolWpf.Imaging.Matching;

/// <summary>
/// 5번 소량 샘플 분류 · OOD 이상 탐지(Few-Shot) 알고리즘.
/// </summary>
public sealed class FewShotRoiAlgorithm : RoiAlgorithmBase
{
    private const int PatchWidth = 48;
    private const int PatchHeight = 48;
    private const int CropMarginPixels = 12;
    private const float ReferenceOodSigmaDenominator = 3.5f; // [Math Why]: Normalizes user OOD multiplier against the 3.5σ Mahalanobis baseline of EmbeddingFewShotClassifier.
    private const float MinAffineScale = 0.25f;
    private const float MaxAffineScale = 4.0f;
    private const int EmbeddingGridX = 2;
    private const int EmbeddingGridY = 2;
    private const int BinsPerCell = 6;
    private const int IntensityMomentBinOffset = 2;
    private const int EnergyMomentBinOffset = 1;
    private const int MaxOrientationBinOffset = 3;
    private const float HalfPi = MathF.PI * 0.5f;
    private const float ByteReciprocal = 1.0f / 255.0f;
    private const float GradientEnergyScale = 0.01f;
    private const float PercentMultiplier = 100.0f;
    private const double MicrosecondsPerSecond = 1_000_000.0;
    private const float PrimarySubpixelJitter = 0.35f;
    private const float SecondarySubpixelJitter = 0.25f;

    public override InspectionSuiteAlgorithmKind Kind => InspectionSuiteAlgorithmKind.FewShot;

    public override InspectionSuiteCategory Category => InspectionSuiteCategory.AiAndAutoTeaching;

    public override string ShortName => "5. 소량 샘플 분류 · OOD 이상 탐지 (Few-Shot)";

    public override string ShortPillLabel => "Few-Shot 분류";

    public override string AssignmentBadge => "스위트 · FewShot";

    public override string Description =>
        "다중 구역 기울기 모멘트 + SimpleShot 정규화 + Ledoit-Wolf 수축 공분산 마할라노비스 거리로 소량 샘플 분류 및 미등록 이상치(OOD) 기각을 수행합니다.";

    public override string UsageGuide =>
        "① 정상 ROI는 '정상 기준 등록', 불량 ROI는 '불량 기준 등록'으로 등록하세요 (샘플 열기 시 자동 등록).\n② 검사 실행 시 정상(Good), 등록 불량(Defect), 미등록 이상치(OOD)를 마할라노비스 거리로 즉시 판정합니다.";

    public override string SampleSceneSlug => "fewshot";

    public override string SampleSceneTitle => "5. Few-Shot 마할라노비스·OOD 기각 합성 샘플 (1200×800)";

    public override bool UsesReferenceRois => true;

    public override InspectionAlgorithmHelpSpec HelpSpec { get; } = new(
        "스위트 내장 소량 샘플 마할라노비스 분류기 (SimpleShot + Ledoit-Wolf OOD)",
        "다중 구역 기울기 히스토그램 · L2 중심 정규화 · 수축 공분산 마할라노비스 거리",
        "• 수식 원리: ROI를 다중 공간 셀로 분할하여 방향성 에지 에너지와 명암 모멘트를 추출한 뒤, SimpleShot 평균 차감·L2 정규화와 Ledoit-Wolf 수축 공분산 역행렬 Σ^{-1}을 통해 정상/불량/미등록 이상치(OOD)를 분류합니다.",
        "① 양품 ROI를 선택해 [정상(Good) 등록], 불량 ROI를 선택해 [불량(NG) 등록]을 누릅니다.\n" +
        "② [전체 ROI 실행]을 누르면 각 ROI의 클래스 판정과 마할라노비스 거리가 계산됩니다.",
        "• OOD 시그마 배수 (1.5~10.0): 미등록 이상치 기각 민감도를 조절합니다.\n" +
        "• 서브 클러스터 수 (1~6): 클래스 내 외관 변형이 다양할 때 2~3으로 설정하세요.",
        "• 전용 탭의 [소량 분류 (Few-Shot)]에서도 동일한 계열의 GeM 풀링 기반 심층 분류를 사용할 수 있습니다.");

    public override string FormatParameterSummary(InspectionSuiteParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        FewShotSuiteOptions opt = parameters.FewShot;
        return $"OOD 기각 배수={opt.OodSigmaMultiplier:F1}σ · 클래스당 서브클러스터 K={opt.MaxSubClusters}개 · SimpleShot + Ledoit-Wolf";
    }

    public override ImmutableArray<InspectionSuiteItemResult> Execute(RoiInspectionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        IReadOnlyList<InspectionSuiteRoiInput> targets = context.TargetRois;
        IReadOnlyList<InspectionSuiteRoiInput> references = context.ReferenceRois;
        FewShotSuiteOptions opt = context.Parameters.FewShot.Validate();

        ReadOnlySpan<(float Dx, float Dy)> subpixelOffsets =
        [
            (0.0f, 0.0f),
            (-PrimarySubpixelJitter, -SecondarySubpixelJitter),
            (PrimarySubpixelJitter, SecondarySubpixelJitter),
            (-SecondarySubpixelJitter, PrimarySubpixelJitter),
            (SecondarySubpixelJitter, -PrimarySubpixelJitter)
        ];

        IReadOnlyList<InspectionSuiteRoiInput> sourceRefs = references.Count > 0 ? references : [targets[0]];
        var trainingExamples = new List<FewShotTrainingExample>(sourceRefs.Count * subpixelOffsets.Length);

        for (int i = 0; i < sourceRefs.Count; i++)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            InspectionSuiteRoiInput refRoi = sourceRefs[i];
            for (int j = 0; j < subpixelOffsets.Length; j++)
            {
                (float dx, float dy) = subpixelOffsets[j];
                float[] emb = ExtractCompactGeMEmbedding(
                    context.Reader,
                    context.ImageWidth,
                    context.ImageHeight,
                    refRoi,
                    PatchWidth,
                    PatchHeight,
                    context.Workspace,
                    context.CancellationToken,
                    dx,
                    dy);
                trainingExamples.Add(new FewShotTrainingExample(refRoi.ClassLabel, emb));
            }
        }

        float oodScale = opt.OodSigmaMultiplier / ReferenceOodSigmaDenominator;
        EmbeddingFewShotClassifier model = EmbeddingFewShotClassifier.Train(trainingExamples.ToArray(), maxSubClustersPerClass: opt.MaxSubClusters);

        var results = ImmutableArray.CreateBuilder<InspectionSuiteItemResult>(targets.Count);
        for (int i = 0; i < targets.Count; i++)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            InspectionSuiteRoiInput target = targets[i];
            long startTick = Stopwatch.GetTimestamp();

            float[] queryEmb = ExtractCompactGeMEmbedding(
                context.Reader,
                context.ImageWidth,
                context.ImageHeight,
                target,
                PatchWidth,
                PatchHeight,
                context.Workspace,
                context.CancellationToken);
            FewShotPrediction pred = model.Predict(queryEmb, context.Workspace);
            float effectiveThreshold = pred.RejectionThreshold * oodScale;
            bool isOod = pred.MahalanobisDistance > effectiveThreshold;

            double elapsedUs = (Stopwatch.GetTimestamp() - startTick) * MicrosecondsPerSecond / Stopwatch.Frequency;
            bool isPass = !isOod && pred.PredictedClassLabel == 0;
            string classTag = isOod
                ? "OOD(미등록 이상치)"
                : (pred.PredictedClassLabel == 0 ? "Good(정상)" : $"Class {pred.PredictedClassLabel}(불량)");

            var builder = new InspectionItemResultBuilder(target);
            if (isPass)
            {
                builder.AddPrimaryPoint(target.Center);
            }
            else
            {
                builder.AddDefectPoint(target.Center);
            }

            results.Add(builder.Build(
                isPass,
                $"{classTag} · D_M={pred.MahalanobisDistance:F2}",
                $"{classTag} · 마할라노비스 거리={pred.MahalanobisDistance:F3} (임계치={effectiveThreshold:F2}) · 신뢰도={pred.ConfidenceScore * PercentMultiplier:F1}%",
                elapsedUs));
        }

        return results.ToImmutable();
    }

    private static float[] ExtractCompactGeMEmbedding(
        MatchingImageReader reader,
        int imageWidth,
        int imageHeight,
        InspectionSuiteRoiInput roi,
        int patchW,
        int patchH,
        InspectionWorkspace workspace,
        CancellationToken token,
        float subpixelOffsetX = 0.0f,
        float subpixelOffsetY = 0.0f)
    {
        LocalRoiCrop localCrop = ReadLocalCrop(reader, imageWidth, imageHeight, roi, CropMarginPixels, token);
        float scale = (float)Math.Sqrt((roi.Width * roi.Height) / ((double)patchW * patchH));
        var pose = new FixturePose2D(
            localCrop.LocalCenter.X + subpixelOffsetX,
            localCrop.LocalCenter.Y + subpixelOffsetY,
            (float)roi.AngleRadians,
            Math.Clamp(scale, MinAffineScale, MaxAffineScale));
        Span<float> patch = workspace.GetFloatBufferA(patchW * patchH);
        SimdAffineWarper.WarpRectifiedRoi(
            localCrop.Crop.Pixels.Span,
            localCrop.Crop.Width,
            localCrop.Crop.Height,
            localCrop.Crop.Stride,
            pose,
            patchW,
            patchH,
            patch);

        float[] embedding = new float[EmbeddingGridX * EmbeddingGridY * BinsPerCell];
        int cellW = patchW / EmbeddingGridX;
        int cellH = patchH / EmbeddingGridY;
        int orientationBinCount = BinsPerCell - IntensityMomentBinOffset;
        int maxOrientationBin = BinsPerCell - MaxOrientationBinOffset;

        for (int cy = 0; cy < EmbeddingGridY; cy++)
        {
            for (int cx = 0; cx < EmbeddingGridX; cx++)
            {
                int baseIdx = ((cy * EmbeddingGridX) + cx) * BinsPerCell;
                int yStart = Math.Max(1, cy * cellH);
                int yEnd = Math.Min(patchH - 1, (cy + 1) * cellH);
                int xStart = Math.Max(1, cx * cellW);
                int xEnd = Math.Min(patchW - 1, (cx + 1) * cellW);

                for (int y = yStart; y < yEnd; y++)
                {
                    for (int x = xStart; x < xEnd; x++)
                    {
                        float gx = patch[(y * patchW) + x + 1] - patch[(y * patchW) + x - 1];
                        float gy = patch[((y + 1) * patchW) + x] - patch[((y - 1) * patchW) + x];
                        float mag = MathF.Sqrt((gx * gx) + (gy * gy));
                        float angle = MathF.Atan2(MathF.Abs(gy), MathF.Abs(gx));
                        int bin = Math.Clamp((int)(angle / HalfPi * orientationBinCount), 0, maxOrientationBin);
                        embedding[baseIdx + bin] += mag;
                        embedding[baseIdx + orientationBinCount] += patch[(y * patchW) + x] * ByteReciprocal;
                        embedding[baseIdx + (BinsPerCell - EnergyMomentBinOffset)] += mag * mag * GradientEnergyScale;
                    }
                }
            }
        }

        return embedding;
    }
}
