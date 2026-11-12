using System.Collections.Immutable;
using System.Numerics;
using VisionAlgorithm.OpenSrc.BlobRle;
using VisionAlgorithm.OpenSrc.Common;
using VisionGeometry;

namespace TeachingToolWpf.Imaging.Matching;

/// <summary>
/// 15번 RLE 고급 위상·형상 기술자 및 Meijster O(N) EDT 워터셰드 접촉 입자 분리 알고리즘.
/// </summary>
public sealed class AdvancedBlobWatershedRoiAlgorithm : RoiAlgorithmBase
{
    private const int CropMarginPixels = 0;
    private const int MinConvexHullVertices = 3;
    private const double HalfFactor = 0.5;
    private const double RadiansToDegrees = 180.0 / Math.PI;

    public override InspectionSuiteAlgorithmKind Kind => InspectionSuiteAlgorithmKind.AdvancedBlobWatershed;

    public override InspectionSuiteCategory Category => InspectionSuiteCategory.SurfaceBlobAnd3D;

    public override string ShortName => "15. [본검사] RLE 고급 형상(Convex Hull·MinAreaRect·Hole) · 거리변환 접촉 블롭 분리";

    public override string ShortPillLabel => "워터셰드 블롭 분리";

    public override string AssignmentBadge => "스위트 · 고급블롭/분리";

    public override string Description =>
        "RLE 스캔라인 위상 해석(Euler 내부 홀 개수·홀 면적), Monotone Chain 볼록 껍질(Convex Hull·Solidity), Rotating Calipers 최소면적 회전 외접 사각형, 명암 통계(μ,σ,min,max) 및 Meijster O(N) 유클리드 거리변환(EDT) 접촉 블롭 워터셰드 분리를 수행합니다.";

    public override string UsageGuide =>
        "① 내부 홀이 있는 브래킷이나 서로 맞닿은 솔더볼/입자 군집 위에 ROI를 배치하세요 (이름에 '워터셰드' 또는 '분리' 포함 시 EDT 워터셰드 자동 활성).\n② 볼록 껍질(Convex Hull), 최소면적 회전 박스(MinAreaRect), 홀 개수·명암 통계(μ,σ)와 분리된 개별 블롭이 시각화됩니다.";

    public override string SampleSceneSlug => "watershed";

    public override string SampleSceneTitle => "15. [본검사] RLE 고급 형상·접촉 블롭 EDT 워터셰드 분리 샘플 (1200×800)";

    public override InspectionAlgorithmHelpSpec HelpSpec { get; } = new(
        "[블롭/분리] 고급 RLE 위상·형상 기술자 & Meijster O(N) EDT 워터셰드 접촉 입자 분리",
        "RLE 오일러 내부 홀 · Monotone Chain 볼록 껍질 · Rotating Calipers 최소 외접 사각형 · Meijster 거리변환 워터셰드",
        "• 수식 원리:\n" +
        "  1) 고급 RLE 블롭: 8-연결 전경과 4-연결 배경 위상 해석으로 내부 구멍 개수(Euler Number = C - H)와 홀 면적을 정확히 세고, Andrew's Monotone Chain 볼록 껍질(Convexity, Solidity), O(H) Rotating Calipers 최소면적 회전 외접 사각형(MinAreaRect), 그레이스케일 명암 모멘트(μ, σ, min, max)를 산출합니다.\n" +
        "  2) 접촉 블롭 워터셰드 분리: 서로 맞닿아 하나로 뭉친 솔더볼/셀 입자들에 대해 Meijster(2000) 2-Pass 포물선 하부 포락선 알고리즘으로 정확한 유클리드 거리 변환(Exact EDT) 맵을 O(N)에 구하고, 각 입자 중심 피크(Local Maxima)에서 마커 제어 등고선 홍수(Flooding)를 수행해 잘록한 목(Neck) 경계선을 정확히 분할합니다.",
        "① 브래킷이나 솔더볼 군집에 ROI를 배치합니다.\n" +
        "② ROI 이름에 '워터셰드' 또는 '분리'가 포함되거나 접촉 입자 샘플을 열면 EDT 워터셰드 분리가 작동하여 붙어 있는 입자들을 개별 블롭과 회전 외접 박스로 분리해 줍니다.",
        "• 이진화 최소 밝기 (0~255): 전경 객체 분리 임계값입니다.\n" +
        "• 최소 블롭 면적 (px): 노이즈 입자를 걸러내는 면적 하한입니다.\n" +
        "• 모폴로지 반경 R: 워터셰드 시 최소 시드 피크 분리 거리 및 전처리 반경에 반영됩니다.",
        "• 일반 블롭 분석에서 2~3개의 붙은 솔더볼이 1개의 거대 불량 블롭으로 합쳐질 때 이 알고리즘을 사용하면 각각의 무게중심과 면적을 개별 계측할 수 있습니다.");

    public override string FormatParameterSummary(InspectionSuiteParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        AdvancedBlobOptions opt = parameters.AdvancedBlob;
        return $"워터셰드 임계={opt.ThresholdIntensity} GV · EDT 피크 간격={opt.MinPeakDistancePx:F1}px · 최소 면적={opt.MinimumAreaPixels}px";
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
        AdvancedBlobOptions validated = context.Parameters.AdvancedBlob.Validate();
        return ExecuteEach(context, CropMarginPixels, validated, EvaluateTarget);
    }

    private static InspectionSuiteItemResult EvaluateTarget(
        RoiInspectionContext context,
        InspectionSuiteRoiInput target,
        LocalRoiCrop localCrop,
        AdvancedBlobOptions options,
        Func<double> measureElapsedMicroseconds)
    {
        byte minThresh = (byte)options.ThresholdIntensity;
        long minArea = options.MinimumAreaPixels;
        float minPeakDist = options.MinPeakDistancePx;

        var grayView = new ReadOnlyImageView2D<byte>(localCrop.Crop.Pixels.Span, localCrop.Crop.Width, localCrop.Crop.Height, localCrop.Crop.Stride);
        RleRegion region = RleRegion.Threshold(grayView, minThresh, byte.MaxValue);

        bool useWatershed = target.ExecutionMode.AdvancedBlob == AdvancedBlobVariant.WatershedSplit;
        List<AdvancedBlobFeature> blobs = useWatershed
            ? AdvancedRleBlobExtractor.SeparateTouchingBlobsWatershed(
                region,
                grayView,
                minPeakSuppressionDistancePixels: minPeakDist,
                minimumAreaPixels: minArea,
                workspace: context.Workspace)
            : AdvancedRleBlobExtractor.ExtractFeatures(
                region,
                grayView,
                minimumAreaPixels: minArea,
                workspace: context.Workspace);

        var builder = new InspectionItemResultBuilder(target);
        int totalHoles = 0;

        for (int b = 0; b < blobs.Count; b++)
        {
            AdvancedBlobFeature blob = blobs[b];
            totalHoles += blob.HoleCount;
            builder.AddPrimaryPoint(new PointD(blob.Centroid.X + localCrop.OffsetX, blob.Centroid.Y + localCrop.OffsetY));

            ReadOnlySpan<Vector2> hullSpan = blob.ConvexHullVertices.Span;
            if (hullSpan.Length >= MinConvexHullVertices)
            {
                var hullPoly = ImmutableArray.CreateBuilder<PointD>(hullSpan.Length);
                for (int h = 0; h < hullSpan.Length; h++)
                {
                    hullPoly.Add(new PointD(hullSpan[h].X + localCrop.OffsetX, hullSpan[h].Y + localCrop.OffsetY));
                }

                builder.AddContour(hullPoly.MoveToImmutable());
            }

            (double sinB, double cosB) = Math.SinCos(blob.MinAreaBoxAngleRad);
            double hw = blob.MinAreaBoxWidth * HalfFactor;
            double hh = blob.MinAreaBoxHeight * HalfFactor;
            double cx = blob.MinAreaBoxCenter.X + localCrop.OffsetX;
            double cy = blob.MinAreaBoxCenter.Y + localCrop.OffsetY;
            builder.AddContour([
                new PointD(cx + (-hw * cosB) - (-hh * sinB), cy + (-hw * sinB) + (-hh * cosB)),
                new PointD(cx + (hw * cosB) - (-hh * sinB), cy + (hw * sinB) + (-hh * cosB)),
                new PointD(cx + (hw * cosB) - (hh * sinB), cy + (hw * sinB) + (hh * cosB)),
                new PointD(cx + (-hw * cosB) - (hh * sinB), cy + (-hw * sinB) + (hh * cosB))
            ]);
        }

        double elapsedUs = measureElapsedMicroseconds();
        bool isPass = blobs.Count > 0;
        string badge = isPass
            ? $"블롭={blobs.Count}개 · 홀={totalHoles}개 · Solidity={blobs[0].Solidity:F2} · μ={blobs[0].MeanGray:F0}GV"
            : "고급 블롭 미검출";
        string detail = isPass
            ? (useWatershed
                ? $"Meijster EDT 워터셰드 분리 완료: 분리 객체={blobs.Count}개 (개별 면적={string.Join("/", blobs.Select(x => $"{x.AreaPixels}px"))}, 원형도={blobs[0].Circularity:F2}, 평균명암={blobs[0].MeanGray:F1}±{blobs[0].StdDevGray:F1}GV)"
                : $"RLE 고급 형상 분석: 객체={blobs.Count}개, 면적={blobs[0].AreaPixels}px, 내부홀={blobs[0].HoleCount}개(홀면적={blobs[0].HoleAreaPixels}px), 볼록도(Solidity)={blobs[0].Solidity:F3}, MinAreaBox={blobs[0].MinAreaBoxWidth:F1}×{blobs[0].MinAreaBoxHeight:F1}px({blobs[0].MinAreaBoxAngleRad * RadiansToDegrees:F1}°), 명암={blobs[0].MeanGray:F1}±{blobs[0].StdDevGray:F1}GV [{blobs[0].MinGray}..{blobs[0].MaxGray}]")
            : "지정된 임계값 이상의 유효 블롭을 찾지 못했습니다.";

        return builder.Build(isPass, badge, detail, elapsedUs);
    }
}
