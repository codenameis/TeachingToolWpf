using System.Collections.Immutable;
using VisionAlgorithm.OpenSrc.BlobRle;
using VisionAlgorithm.OpenSrc.Common;
using VisionAlgorithm.OpenSrc.Preprocessing;
using VisionGeometry;

namespace TeachingToolWpf.Imaging.Matching;

/// <summary>
/// 13번 2D 쌍이차 다항식 조명 평탄화 · Perona-Malik 비등방성 확산 · Vincent 형태학적 재구성 전처리 알고리즘.
/// </summary>
public sealed class ShadingReconRoiAlgorithm : RoiAlgorithmBase
{
    private const int CropMarginPixels = 0;
    private const int DiffusionIterations = 10;
    private const float DiffusionTimeStepLambda = 0.20f;
    private const int PolynomialSubsampleStep = 2;
    private const int MinCombinedReconMarkerRadius = 2;
    private const int PostThresholdBaseOffsetGray = 22;
    private const int MinPostThresholdGray = 100;
    private const int MaxPostThresholdGray = 245;
    private const float MaxAllowedCleanGradientOvershoot = 2.0f;
    private const int QuadrantCount = 4;
    private const int LowerQuadrantRowOffset = 2;

    public override InspectionSuiteAlgorithmKind Kind => InspectionSuiteAlgorithmKind.PreprocessingShadingAndRecon;

    public override InspectionSuiteCategory Category => InspectionSuiteCategory.PreprocessingAndFrequency;

    public override string ShortName => "13. [전처리] 2D 다항식 셰이딩 보정 · 비등방성 확산 · 형태학적 재구성";

    public override string ShortPillLabel => "조명보정·확산·재구성";

    public override string AssignmentBadge => "전처리 · 조명/재구성";

    public override string Description =>
        "HALCON estimate_background_illumination / anisotropic_diffusion / opening_by_reconstruction 급 전처리: 2D 6-기저 쌍이차 다항식 조명 평탄화, Perona-Malik 비등방성 확산, Vincent(1993) 하이브리드 형태학적 재구성을 수행합니다.";

    public override string UsageGuide =>
        "① 불균일 조명/비네팅, 표면 그레인 노이즈, 핀홀/스패터가 있는 영역에 ROI를 배치하세요.\n② 기준 밝기(Threshold)와 마커 반경(R), 확산 대비(κ)를 조절하면 다항식 조명 평탄화·비등방성 확산·Vincent 재구성 전후 배경 편차 감소량과 분리된 객체가 즉시 표시됩니다.";

    public override string SampleSceneSlug => "preprocessing";

    public override string SampleSceneTitle => "13. [전처리] 2D 다항식 조명 평탄화·비등방성 확산·재구성 샘플 (1200×800)";

    public override InspectionAlgorithmHelpSpec HelpSpec { get; } = new(
        "[전처리] 2D 쌍이차 다항식 조명 평탄화 · Perona-Malik 비등방성 확산 · Vincent 형태학 재구성",
        "HALCON estimate_background_illumination + anisotropic_diffusion + opening_by_reconstruction 급",
        "• 수식 원리:\n" +
        "  1) 조명 평탄화(Shading Correction): 6-기저 2차 다항식 B(u,v) = c_0 + c_1 u + c_2 v + c_3 u² + c_4 uv + c_5 v²를 Cholesky 정규방정식(A^T A c = A^T b)으로 최소제곱 피팅하여 불균일 조명/비네팅을 제거합니다(I_flat = clamp(I - B + μ_B)).\n" +
        "  2) 비등방성 확산(Perona-Malik): ∂I/∂t = div(g(‖∇I‖)∇I), 전도 함수 g(s) = 1 / (1 + (s/κ)²)를 통해 미세 에지(|∇I| > κ)는 칼같이 보존하면서 표면 그레인 노이즈만 평활화합니다.\n" +
        "  3) 형태학적 재구성(Vincent 1993): 마커 침식 후 마스크 내부에서 래스터/안티래스터 스캔 + FIFO 파동 전파로 측지적 팽창(Geodesic Dilation)을 수렴시켜, 살아남은 객체의 원래 윤곽 손실 없이 작은 스패터/핀홀만 100% 소거합니다.",
        "① 조명 그라데이션이나 표면 결함·핀홀이 섞인 영역에 ROI를 배치합니다.\n" +
        "② [이진화 밝기], [모폴로지 마커 반경(R)], [최소 에지 대비(κ)]를 조절하면 3단계 전처리 후 배경 표준편차(σ) 감소율과 순수 객체 추출 결과가 즉시 표시됩니다.",
        "• 최소 에지 대비 (확산 κ, 5~100): 보존할 에지의 기울기 문턱값입니다.\n" +
        "• 모폴로지 반경 R (1~8 px): 제거할 스패터/노이즈 입자의 최대 반경입니다(이 반경보다 큰 본체 부품은 모서리가 깎이지 않고 원형 그대로 복원됩니다).\n" +
        "• 이진화 임계값 (0~255): 조명 평탄화 후 전경 마스크 분리 기준입니다.",
        "• 일반 모폴로지 Opening은 사각 패드의 모서리를 둥글게 깎아버리지만, Vincent 형태학적 재구성(Opening by Reconstruction)은 노이즈만 지우고 패드 모서리 직각을 완벽히 보존합니다!");

    public override string FormatParameterSummary(InspectionSuiteParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ShadingReconOptions opt = parameters.ShadingRecon;
        return $"다항식 목표 배경={opt.TargetBaseGray} GV · 비등방성 확산 κ={opt.AnisotropicDiffusionKappa:F1} · 재구성 마커 R={opt.ReconstructionMarkerRadius} px";
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
        ShadingReconOptions validated = context.Parameters.ShadingRecon.Validate();
        return ExecuteEach(context, CropMarginPixels, validated, EvaluateTarget);
    }

    private InspectionSuiteItemResult EvaluateTarget(
        RoiInspectionContext context,
        InspectionSuiteRoiInput target,
        LocalRoiCrop localCrop,
        ShadingReconOptions options,
        Func<double> measureElapsedMicroseconds)
    {
        byte targetBase = (byte)options.TargetBaseGray;
        int markerRadius = options.ReconstructionMarkerRadius;
        float kappa = options.AnisotropicDiffusionKappa;

        var srcView = new ReadOnlyImageView2D<byte>(localCrop.Crop.Pixels.Span, localCrop.Crop.Width, localCrop.Crop.Height, localCrop.Crop.Stride);
        int totalPixels = checked(localCrop.Crop.Width * localCrop.Crop.Height);
        byte[] dstBuf = new byte[totalPixels];
        byte[] stageBuf = new byte[totalPixels];

        string modeTag = ApplySelectedFilter(
            target.ExecutionMode.ShadingVariant,
            srcView,
            localCrop.Crop.Width,
            localCrop.Crop.Height,
            targetBase,
            markerRadius,
            kappa,
            stageBuf,
            dstBuf);

        ComputeGraySpreadStats(srcView, out float rawStd, out float rawGradientRange);
        var outView = new ReadOnlyImageView2D<byte>(dstBuf, localCrop.Crop.Width, localCrop.Crop.Height, localCrop.Crop.Width);
        ComputeGraySpreadStats(outView, out float cleanStd, out float cleanGradientRange);

        byte postThreshold = (byte)Math.Clamp(targetBase + PostThresholdBaseOffsetGray, MinPostThresholdGray, MaxPostThresholdGray);
        RleRegion postRegion = RleRegion.Threshold(outView, minIntensity: postThreshold, maxIntensity: byte.MaxValue);
        List<BlobComponentFeature> postBlobs = RleBlobAnalyzer.AnalyzeComponents(
            postRegion,
            minimumAreaPixels: options.MinimumBlobAreaPixels,
            context.Workspace);

        var builder = new InspectionItemResultBuilder(target)
            .WithGuideContours(BuildGeometricGuides(target, context.Parameters));

        for (int b = 0; b < postBlobs.Count; b++)
        {
            BlobComponentFeature blob = postBlobs[b];
            builder.AddPrimaryPoint(new PointD(blob.Centroid.X + localCrop.OffsetX, blob.Centroid.Y + localCrop.OffsetY));
            double left = blob.MinCol + localCrop.OffsetX;
            double top = blob.MinRow + localCrop.OffsetY;
            double right = left + blob.BoundingWidth;
            double bottom = top + blob.BoundingHeight;
            builder.AddContour([new PointD(left, top), new PointD(right, top), new PointD(right, bottom), new PointD(left, bottom)]);
        }

        if (builder.PrimaryPointCount == 0)
        {
            builder.AddPrimaryPoint(target.Center);
        }

        double elapsedUs = measureElapsedMicroseconds();
        bool isPass = cleanGradientRange <= rawGradientRange + MaxAllowedCleanGradientOvershoot;
        string badge = $"배경편차 {rawGradientRange:F1}→{cleanGradientRange:F1}GV · 검출={postBlobs.Count}개";
        string detail = $"{modeTag} 완료 · 배경 구배 편차 {rawGradientRange:F1}→{cleanGradientRange:F1} GV (전역 σ {rawStd:F1}→{cleanStd:F1}) · 전처리 후 유효 객체 {postBlobs.Count}개 분리";

        return builder.Build(isPass, badge, detail, elapsedUs);
    }

    private static string ApplySelectedFilter(
        ShadingFilterVariant variant,
        ReadOnlyImageView2D<byte> srcView,
        int width,
        int height,
        byte targetBase,
        int markerRadius,
        float kappa,
        byte[] stageBuf,
        byte[] dstBuf)
    {
        switch (variant)
        {
            case ShadingFilterVariant.AnisotropicDiffusion:
            {
                var diffSpec = AnisotropicDiffusionSpec.Create(
                    iterations: DiffusionIterations,
                    kappaContrast: kappa,
                    timeStepLambda: DiffusionTimeStepLambda);
                ShadingAndReconstructionEngine.ApplyAnisotropicDiffusion(srcView, in diffSpec, dstBuf, width);
                return $"Perona-Malik 비등방성 확산 (κ={kappa:F1}, Iter={DiffusionIterations})";
            }

            case ShadingFilterVariant.MorphologicalReconstruction:
            {
                var reconSpec = MorphologicalReconstructionSpec.Create(markerRadius: markerRadius, isFillDarkHoles: false);
                ShadingAndReconstructionEngine.ApplyMorphologicalReconstruction(srcView, in reconSpec, dstBuf, width);
                return $"Vincent 형태학적 재구성 (R={markerRadius}px)";
            }

            default:
            {
                var shadingSpec = PolynomialShadingSpec.Create(
                    targetBaseGray: targetBase,
                    isMultiplicativeGain: false,
                    subsampleStep: PolynomialSubsampleStep);
                ShadingAndReconstructionEngine.ApplyPolynomialShadingCorrection(srcView, in shadingSpec, stageBuf, width);
                int effectiveRadius = Math.Max(MinCombinedReconMarkerRadius, markerRadius);
                var reconSpec = MorphologicalReconstructionSpec.Create(markerRadius: effectiveRadius, isFillDarkHoles: false);
                ShadingAndReconstructionEngine.ApplyMorphologicalReconstruction(
                    new ReadOnlyImageView2D<byte>(stageBuf, width, height, width),
                    in reconSpec,
                    dstBuf,
                    width);
                return $"2D 다항식 조명 평탄화(Base={targetBase}) + 재구성(R={effectiveRadius})";
            }
        }
    }

    private static void ComputeGraySpreadStats(ReadOnlyImageView2D<byte> view, out float stdDev, out float quadrantSpread)
    {
        int width = view.Width;
        int height = view.Height;
        int halfWidth = Math.Max(1, width / 2);
        int halfHeight = Math.Max(1, height / 2);

        double sum = 0.0;
        double sumSq = 0.0;
        Span<double> qSum = stackalloc double[QuadrantCount];
        Span<int> qCount = stackalloc int[QuadrantCount];
        qSum.Clear();
        qCount.Clear();

        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<byte> row = view.GetRowSpan(y);
            int qy = y < halfHeight ? 0 : LowerQuadrantRowOffset;
            for (int x = 0; x < width; x++)
            {
                byte value = row[x];
                sum += value;
                sumSq += value * value;
                int qIdx = qy + (x < halfWidth ? 0 : 1);
                qSum[qIdx] += value;
                qCount[qIdx]++;
            }
        }

        int total = width * height;
        double mean = sum / total;
        stdDev = (float)Math.Sqrt(Math.Max(0.0, (sumSq / total) - (mean * mean)));

        double minQ = double.MaxValue;
        double maxQ = double.MinValue;
        for (int q = 0; q < QuadrantCount; q++)
        {
            if (qCount[q] > 0)
            {
                double qMean = qSum[q] / qCount[q];
                if (qMean < minQ)
                {
                    minQ = qMean;
                }

                if (qMean > maxQ)
                {
                    maxQ = qMean;
                }
            }
        }

        quadrantSpread = maxQ >= minQ ? (float)(maxQ - minQ) : 0.0f;
    }
}
