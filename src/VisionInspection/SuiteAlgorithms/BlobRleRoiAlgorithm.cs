using System.Collections.Immutable;
using VisionAlgorithm.OpenSrc.BlobRle;
using VisionGeometry;

namespace TeachingToolWpf.Imaging.Matching;

/// <summary>
/// 3번 RLE 스캔라인 블롭 해석 및 구간 모폴로지 알고리즘.
/// </summary>
public sealed class BlobRleRoiAlgorithm : RoiAlgorithmBase
{
    private const int CropMarginPixels = 0;
    private const byte MaxIntensityByte = byte.MaxValue;
    private const double MinPrincipalAxisHalfLengthPx = 6.0;
    private const double PrincipalAxisLengthRatio = 0.45;
    private const double RadiansToDegrees = 180.0 / Math.PI;

    public override InspectionSuiteAlgorithmKind Kind => InspectionSuiteAlgorithmKind.BlobRle;

    public override InspectionSuiteCategory Category => InspectionSuiteCategory.SurfaceBlobAnd3D;

    public override string ShortName => "3. RLE 블롭 해석 · 구간 모폴로지";

    public override string ShortPillLabel => "RLE 블롭·모폴로지";

    public override string AssignmentBadge => "스위트 · 블롭RLE";

    public override string Description =>
        "스캔라인 런-렝스 인코딩(RLE) 압축 영역에서 구간 모폴로지(Opening)와 그린 정리(Green's Theorem) 2차 모멘트로 무게중심·주축 각도를 추출합니다.";

    public override string UsageGuide =>
        "① 이진화 최소 밝기(Threshold)와 모폴로지 오프닝 반경(R) 슬라이더를 조절해 배경 스펙클 노이즈를 제거하세요.\n② 검출된 각 블롭의 바운딩 박스, 그린 정리 서브픽셀 무게중심(청록 점), 주축 방향선이 실시간으로 표시됩니다.";

    public override string SampleSceneSlug => "blob";

    public override string SampleSceneTitle => "3. RLE 블롭 해석·구간 모폴로지 합성 샘플 (1200×800)";

    public override InspectionAlgorithmHelpSpec HelpSpec { get; } = new(
        "고속 RLE 스캔라인 블롭 분석 (HALCON connection + area_center_gray 급)",
        "스캔라인 런-렝스 인코딩(RLE) · 구간 모폴로지 Opening · 그린 정리(Green's Theorem) 2차 모멘트 주축",
        "• 수식 원리: 2D 픽셀 배열 대신 수평 연속 구간(Run-LengthInterval)으로 압축하여 4/8-연결 성분을 고속 병합하고, RLE 구간 연산으로 침식·팽창(Opening)을 수행합니다.\n" +
        "• 면적 M_00, 무게중심 (x̄, ȳ) = (M_10/M_00, M_01/M_00), 중앙 2차 모멘트(μ_20, μ_02, μ_11)로부터 주축 각도 θ = 0.5·atan2(2μ_11, μ_20 - μ_02)와 이심률(Eccentricity)을 정확히 계산합니다.",
        "① 블롭을 추출할 영역에 ROI를 배치합니다.\n" +
        "② [이진화 최소 밝기(Threshold)]와 [모폴로지 반경(R)]을 조절하면 배경 노이즈가 제거된 유효 블롭들의 무게중심과 주축선이 표시됩니다.",
        "• 이진화 임계값 (0~255): 전경 블롭이 분리되는 밝기 기준입니다.\n" +
        "• 모폴로지 반경 R (0~10 px): 1~2 px로 설정하면 미세 핀홀/스펙클 노이즈가 깔끔하게 제거됩니다.\n" +
        "• 최소 블롭 면적 (1~5000 px): 기준 면적 미만의 작은 입자는 결과에서 제외합니다.",
        "• 내부 홀 개수, 볼록 껍질(Convex Hull), 접촉 입자 워터셰드 분리가 필요할 때는 15번 [고급 RLE 블롭 + EDT 워터셰드] 알고리즘을 선택하세요.");

    public override string FormatParameterSummary(InspectionSuiteParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        BlobRleOptions opt = parameters.Blob;
        return $"이진화 임계 밝기={opt.ThresholdIntensity} GV · 모폴로지 반경 R={opt.MorphologyRadius} px · 최소 블롭면적={opt.MinimumAreaPixels}px";
    }

    public override ImmutableArray<InspectionSuiteItemResult> Execute(RoiInspectionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        BlobRleOptions opt = context.Parameters.Blob.Validate();
        return ExecuteEach(context, CropMarginPixels, opt, EvaluateTarget);
    }

    private static InspectionSuiteItemResult EvaluateTarget(
        RoiInspectionContext context,
        InspectionSuiteRoiInput target,
        LocalRoiCrop localCrop,
        BlobRleOptions opt,
        Func<double> measureElapsedMicroseconds)
    {
        var (crop, _, offsetX, offsetY) = localCrop;
        byte minThresh = (byte)opt.ThresholdIntensity;
        RleRegion region = RleRegion.Threshold(crop.Pixels.Span, crop.Width, crop.Height, crop.Stride, minIntensity: minThresh, maxIntensity: MaxIntensityByte);
        if (opt.MorphologyRadius > 0)
        {
            region = RleMorphology.OpenBox(region, opt.MorphologyRadius, opt.MorphologyRadius);
        }

        List<BlobComponentFeature> blobs = RleBlobAnalyzer.AnalyzeComponents(region, minimumAreaPixels: opt.MinimumAreaPixels, context.Workspace);
        var builder = new InspectionItemResultBuilder(target);

        long totalArea = 0;
        for (int b = 0; b < blobs.Count; b++)
        {
            BlobComponentFeature blob = blobs[b];
            totalArea += blob.AreaPixels;
            PointD worldCentroid = new(blob.Centroid.X + offsetX, blob.Centroid.Y + offsetY);
            builder.AddPrimaryPoint(worldCentroid);

            double left = blob.MinCol + offsetX;
            double top = blob.MinRow + offsetY;
            double right = left + blob.BoundingWidth;
            double bottom = top + blob.BoundingHeight;
            builder.AddContour([
                new PointD(left, top),
                new PointD(right, top),
                new PointD(right, bottom),
                new PointD(left, bottom)
            ]);

            double axisLen = Math.Max(MinPrincipalAxisHalfLengthPx, Math.Min(blob.BoundingWidth, blob.BoundingHeight) * PrincipalAxisLengthRatio);
            double dx = Math.Cos(blob.PrincipalAxisAngleRad) * axisLen;
            double dy = Math.Sin(blob.PrincipalAxisAngleRad) * axisLen;
            builder.AddContour([
                new PointD(worldCentroid.X - dx, worldCentroid.Y - dy),
                new PointD(worldCentroid.X + dx, worldCentroid.Y + dy)
            ]);
        }

        double elapsedUs = measureElapsedMicroseconds();
        bool isPass = blobs.Count > 0;
        string topBlobInfo = blobs.Count > 0
            ? $"최대 면적={blobs[0].AreaPixels}px, 중심=({blobs[0].Centroid.X + offsetX:F2}, {blobs[0].Centroid.Y + offsetY:F2}), 주축={blobs[0].PrincipalAxisAngleRad * RadiansToDegrees:F1}°, 이심률={blobs[0].Eccentricity:F3}"
            : "조건 만족 블롭 없음";

        return builder.Build(
            isPass,
            $"RLE Blobs={blobs.Count} · Area={totalArea}px",
            $"검출 블롭={blobs.Count}개 (총 면적={totalArea}px, 런 수={region.Runs.Count}) · {topBlobInfo}",
            elapsedUs);
    }
}
