using System.Collections.Immutable;
using VisionAlgorithm.OpenSrc.BlobRle;
using VisionAlgorithm.OpenSrc.Common;
using VisionAlgorithm.OpenSrc.Preprocessing;
using VisionGeometry;

namespace TeachingToolWpf.Imaging.Matching;

/// <summary>
/// 17번 2D Radix-2 FFT 주기적 모아레 노치 · 호모모픽 조명 분리 · 2D Gabor 텍스처 전처리 알고리즘.
/// </summary>
public sealed class FftAndGaborRoiAlgorithm : RoiAlgorithmBase
{
    private const int CropMarginPixels = 0;
    private const double HomomorphicGammaLow = 0.35;
    private const double MinHomomorphicSigma = 6.0;
    private const double HomomorphicSigmaScale = 8.0;
    private const double MinGaborWavelengthPx = 4.0;
    private const double GaborWavelengthSigmaScale = 5.0;
    private const double MinGaborEnvelopeSigma = 2.0;
    private const double GaborEnvelopeSigmaScale = 2.2;
    private const double GaborAspectRatio = 0.55;
    private const int FftMinFrequencyRadius = 5;
    private const double MinFftNotchSigma = 2.5;
    private const double FftNotchSigmaScale = 2.5;
    private const int PostOtsuThresholdOffsetGray = 12;
    private const int MinPostThresholdGray = 80;
    private const int MaxPostThresholdGray = 240;
    private const double MinPassStdDevIntensity = 1.0;
    private const double RadiansToDegrees = 180.0 / Math.PI;

    public override InspectionSuiteAlgorithmKind Kind => InspectionSuiteAlgorithmKind.PreprocessingFftAndGabor;

    public override InspectionSuiteCategory Category => InspectionSuiteCategory.PreprocessingAndFrequency;

    public override string ShortName => "17. [전처리] 2D FFT 주기적 모아레 노치 · 호모모픽 조명 분리 · Gabor 텍스처";

    public override string ShortPillLabel => "2D FFT·호모모픽·Gabor";

    public override string AssignmentBadge => "전처리 · FFT/Gabor";

    public override string Description =>
        "2D Radix-2 FFT 주기적 모아레/메쉬 스펙트럼 노치 감쇠, 호모모픽 ln(I+1) 조명·반사율 분리, 2D 직교 위상 Gabor 텍스처 강조를 수행합니다.";

    public override string UsageGuide =>
        "① 주기적 모아레 무늬, 불균일 조명(이름에 '호모모픽'), 또는 방향성 텍스처(이름에 'Gabor') 영역에 ROI를 배치하세요.\n② 주파수 노치 감쇠·조명 분리·Gabor 강조 전후 Otsu 임계값과 분리 객체가 즉시 표시됩니다.";

    public override string SampleSceneSlug => "fftgabor";

    public override string SampleSceneTitle => "17. [전처리] 2D FFT 모아레 노치·호모모픽 조명 분리·Gabor 텍스처 샘플 (1200×800)";

    public override InspectionAlgorithmHelpSpec HelpSpec { get; } = new(
        "[전처리] 2D Radix-2 FFT 주기적 모아레 노치 · 호모모픽 조명 분리 · 2D Gabor 텍스처 필터",
        "Cooley-Tukey 2D FFT 스펙트럼 피크 자동 감쇠 · ln(I+1) 조명-반사율 분리 · 직교 위상 Gabor 뱅크",
        "• 수식 원리:\n" +
        "  1) 2D FFT 주기적 노치 필터: Cooley-Tukey 2D Radix-2 FFT로 주파수 스펙트럼 F(u,v)를 구한 뒤, DC 중심 반경 바깥에서 국소 극대 스펙트럼 스파이크(주기적 모아레·메쉬·직조 격자 간섭)를 자동 탐색하여 켤레 대칭 가우시안 노치 H(u,v) = Π(1 - exp(-D₁²/2σ²))(1 - exp(-D₂²/2σ²))로 감쇠 후 IFFT로 복원합니다.\n" +
        "  2) 호모모픽(Homomorphic) 필터: 영상 I(x,y) = L(x,y)·R(x,y)에 로그 변환 ln(I+1) = ln(L) + ln(R)을 취해 저주파 조명(L)은 γ_L < 1로 억제하고 고주파 반사율 에지(R)는 γ_H > 1로 증폭한 뒤 exp(·)로 복원합니다.\n" +
        "  3) 2D Gabor 텍스처 필터: g(x,y) = exp(-(x'² + γ²y'²)/(2σ²))·cos(2πx'/λ) 직교 위상 에너지 응답으로 특정 방향(θ)과 파장(λ)의 헤어라인 스크래치·결함을 부각합니다.",
        "① 주기적 메쉬/모아레 간섭, 급격한 조명 그림자(이름에 '호모모픽' 포함), 또는 방향성 브러시드 질감(이름에 'Gabor' 포함) 위에 ROI를 배치합니다.\n" +
        "② [선택 ROI 실행]을 누르면 전처리 전후 히스토그램·Otsu 임계값·기울기 변화와 검출된 객체가 즉시 표시됩니다.",
        "• 가우시안 σ (1.2~10): FFT 노치 감쇠 폭, 호모모픽 조명 분리 스케일, Gabor 포락선 폭에 반영됩니다.\n" +
        "• 최소 블롭 면적 (px): 전처리 후 분리된 유효 전경 객체의 최소 크기입니다.",
        "• 이 3가지 주파수·텍스처 필터는 [이미지 필터 & 전처리 파이프라인] 리스트에도 개별 단계로 추가하여 모든 본검사(패턴 매칭·캘리퍼·OCR) 이전에 자동 선행 실행할 수 있습니다!");

    public override string FormatParameterSummary(InspectionSuiteParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        FrequencyFilterOptions opt = parameters.FrequencyFilter;
        return $"FFT 노치/Gabor 대역폭 σ={opt.NotchOrGaborSigma:F2} · 스펙트럼 피크 배수={opt.PeakThresholdRatio:F1}x · 호모모픽 γH={opt.HomomorphicGammaHigh:F2}";
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
        FrequencyFilterOptions raw = context.Parameters.FrequencyFilter;
        return ExecuteEach(context, CropMarginPixels, raw, EvaluateTarget);
    }

    private InspectionSuiteItemResult EvaluateTarget(
        RoiInspectionContext context,
        InspectionSuiteRoiInput target,
        LocalRoiCrop localCrop,
        FrequencyFilterOptions rawOptions,
        Func<double> measureElapsedMicroseconds)
    {
        float sigma = Math.Clamp(rawOptions.NotchOrGaborSigma, FrequencyFilterOptions.MinSigmaBound, FrequencyFilterOptions.MaxSigmaBound);
        double peakRatio = Math.Clamp((double)rawOptions.PeakThresholdRatio, FrequencyFilterOptions.MinPeakRatioBound, FrequencyFilterOptions.MaxPeakRatioBound);
        double gammaHigh = Math.Clamp((double)rawOptions.HomomorphicGammaHigh, FrequencyFilterOptions.MinGammaHighBound, FrequencyFilterOptions.MaxGammaHighBound);
        int minBlobArea = Math.Max(FrequencyFilterOptions.MinPostBlobAreaPixels, rawOptions.MinimumBlobAreaPixels);

        int totalPixels = checked(localCrop.Crop.Width * localCrop.Crop.Height);
        byte[] dstBuf = new byte[totalPixels];

        string modeTag = ApplyFrequencyFilter(
            target.ExecutionMode.FrequencyFilter,
            localCrop.Crop.Pixels.Span,
            localCrop.Crop.Width,
            localCrop.Crop.Height,
            localCrop.Crop.Stride,
            target.AngleRadians,
            sigma,
            peakRatio,
            gammaHigh,
            dstBuf);

        RoiHistogramProfileSummary beforeStats = FrequencyAndTextureFilterEngine.ComputeRoiHistogramAndProfile(
            localCrop.Crop.Pixels.Span,
            localCrop.Crop.Width,
            localCrop.Crop.Height,
            localCrop.Crop.Stride);
        RoiHistogramProfileSummary afterStats = FrequencyAndTextureFilterEngine.ComputeRoiHistogramAndProfile(
            dstBuf,
            localCrop.Crop.Width,
            localCrop.Crop.Height,
            localCrop.Crop.Width);

        var outView = new ReadOnlyImageView2D<byte>(dstBuf, localCrop.Crop.Width, localCrop.Crop.Height, localCrop.Crop.Width);
        byte postThreshold = (byte)Math.Clamp(afterStats.OtsuThreshold + PostOtsuThresholdOffsetGray, MinPostThresholdGray, MaxPostThresholdGray);
        RleRegion postRegion = RleRegion.Threshold(outView, minIntensity: postThreshold, maxIntensity: byte.MaxValue);
        List<BlobComponentFeature> postBlobs = RleBlobAnalyzer.AnalyzeComponents(postRegion, minimumAreaPixels: minBlobArea, context.Workspace);

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
        bool isPass = afterStats.StdDevIntensity > MinPassStdDevIntensity;
        string badge = $"{modeTag} · Otsu={afterStats.OtsuThreshold}GV · 객체={postBlobs.Count}개";
        string detail = $"{modeTag} 완료 · 명암 μ={beforeStats.MeanIntensity:F1}→{afterStats.MeanIntensity:F1}GV, σ={beforeStats.StdDevIntensity:F1}→{afterStats.StdDevIntensity:F1}GV, Otsu={afterStats.OtsuThreshold}GV, 최대기울기={Math.Max(afterStats.HorizontalPeakGradient, afterStats.VerticalPeakGradient):F1} · 검출 객체={postBlobs.Count}개";

        return builder.Build(isPass, badge, detail, elapsedUs);
    }

    private static string ApplyFrequencyFilter(
        FrequencyFilterVariant variant,
        ReadOnlySpan<byte> srcSpan,
        int width,
        int height,
        int stride,
        double angleRadians,
        float sigma,
        double peakRatio,
        double gammaHigh,
        byte[] dstBuf)
    {
        switch (variant)
        {
            case FrequencyFilterVariant.HomomorphicIllumination:
            {
                FrequencyAndTextureFilterEngine.ApplyHomomorphicFilter(
                    srcSpan,
                    width,
                    height,
                    stride,
                    dstBuf,
                    gammaLow: HomomorphicGammaLow,
                    gammaHigh: gammaHigh,
                    sigma: Math.Max(MinHomomorphicSigma, sigma * HomomorphicSigmaScale));
                return $"호모모픽 ln(I+1) 조명·반사율 분리 (γH={gammaHigh:F2})";
            }

            case FrequencyFilterVariant.GaborTexture:
            {
                FrequencyAndTextureFilterEngine.ApplyGaborTextureFilter(
                    srcSpan,
                    width,
                    height,
                    stride,
                    dstBuf,
                    wavelength: Math.Max(MinGaborWavelengthPx, sigma * GaborWavelengthSigmaScale),
                    orientationDeg: angleRadians * RadiansToDegrees,
                    sigma: Math.Max(MinGaborEnvelopeSigma, sigma * GaborEnvelopeSigmaScale),
                    aspectRatio: GaborAspectRatio);
                return $"2D 직교 위상 Gabor 텍스처 강조 (σ={sigma:F1})";
            }

            default:
            {
                int attenuatedPeaks = FrequencyAndTextureFilterEngine.ApplyFftPeriodicNotchFilter(
                    srcSpan,
                    width,
                    height,
                    stride,
                    dstBuf,
                    minFrequencyRadius: FftMinFrequencyRadius,
                    notchSigma: Math.Max(MinFftNotchSigma, sigma * FftNotchSigmaScale),
                    peakThresholdRatio: peakRatio);
                return $"2D Radix-2 FFT 주기적 노치 (감쇠 피크={attenuatedPeaks}쌍, 감도={peakRatio:F1})";
            }
        }
    }
}
