using System.Collections.Immutable;
using VisionAlgorithm.OpenSrc.DataCode2D;
using VisionGeometry;

namespace TeachingToolWpf.Imaging.Matching;

/// <summary>
/// 18번 산업용 Code 128 멀티스캔라인 서브픽셀 디코더 및 ISO/IEC 15416 인쇄 품질 검증 알고리즘.
/// </summary>
public sealed class Barcode1DRoiAlgorithm : RoiAlgorithmBase
{
    private const int CropMarginPixels = 12;
    private const double HalfFactor = 0.5;
    private const double MinWingBackPx = 6.0;
    private const double WingBackHalfWidthRatio = 0.14;
    private const double MinWingSidePx = 4.5;
    private const double WingSideHalfWidthRatio = 0.10;

    public override InspectionSuiteAlgorithmKind Kind => InspectionSuiteAlgorithmKind.Barcode1DReader;

    public override InspectionSuiteCategory Category => InspectionSuiteCategory.Barcode1D2D;

    public override string ShortName => "18. [본검사] 산업용 1D 바코드(Code 128) 멀티스캔 디코더 · ISO/IEC 15416 품질 등급";

    public override string ShortPillLabel => "1D 바코드 리더";

    public override string AssignmentBadge => "스위트 · 1D바코드";

    public override string Description =>
        "다중 스캔라인 서브픽셀 에지 영교차 기반 Code 128B 1D 바코드 디코딩, Modulo-103 체크섬 검증 및 ISO/IEC 15416 인쇄 품질 등급(SC/MOD/DEC Grade A~F)을 산출합니다.";

    public override string UsageGuide =>
        "① 1D 바코드(Code 128 / EAN-13) 좌우 여백을 포함하도록 ROI를 그리세요 (하나의 ROI 안 여러 바코드 동시 판독 지원).\n② 9개 다중 스캔라인 서브픽셀 에지 폭 정규화 → Modulo-103/EAN 체크섬 검증 → 각 바코드별 위치(X, Y)·회전각(θ)과 ISO/IEC 15416 인쇄 등급(A~F)을 모두 출력합니다.";

    public override string SampleSceneSlug => "barcode1d";

    public override string SampleSceneTitle => "18. [본검사] 1D 바코드(Code 128) 멀티스캔 디코더 & ISO 15416 품질 샘플 (1200×800)";

    public override InspectionAlgorithmHelpSpec HelpSpec { get; } = new(
        "[1D 바코드] 산업용 Code 128 멀티스캔라인 서브픽셀 디코더 & ISO/IEC 15416 인쇄 품질 검증",
        "다중 스캔라인 서브픽셀 에지 영교차 · 11-모듈 6-엘리먼트 정규화 · Modulo-103 체크섬 · ISO 15416 Grade A~F",
        "• 수식 원리:\n" +
        "  1) 멀티스캔 서브픽셀 폭 추출: ROI 높이를 가로지르는 9개 수평 스캔라인에서 적응형 중앙값 문턱(I_mid)에 대한 선형 보간 서브픽셀 에지 전이 위치 x_k를 구하고 연속된 흑(Bar)/백(Space) 폭을 측정합니다.\n" +
        "  2) Code 128B & Modulo-103 검증: 6개 엘리먼트(3 Bar + 3 Space = 11 모듈) 단위로 정규화하여 ISO/IEC 15417 107종 패턴 테이블과 유클리드 최소 거리 매칭을 수행하고, (StartCode + Σ i·C_i) mod 103 체크섬으로 오판독을 0%로 차단합니다.\n" +
        "  3) ISO/IEC 15416 품질 등급: 기호 대비(Symbol Contrast, SC = (R_max - R_min)/255), 변조도(Modulation, MOD = MinEdgeContrast / SC), 디코더빌리티(Decodability, DEC)를 종합하여 Grade A(4.0) ~ F(0.0)를 산출합니다.",
        "① 라벨이나 부품에 인쇄된 1D 바코드(좌우 여백 Quiet Zone 포함)를 감싸도록 ROI를 배치합니다.\n" +
        "② [선택 ROI 실행]을 누르면 판독 문자열, Modulo-103 체크섬 검증 결과, 유효 스캔선 수, ISO/IEC 15416 품질 등급(A~F)이 즉시 출력됩니다.",
        "• 바코드 일부 높이에 스크래치나 오염이 있어도 9개 다중 스캔라인 중 정상 라인들을 투표/통합하여 안정적으로 판독합니다.",
        "• 인쇄 번짐으로 바 두께가 변형되면 DEC(Decodability) 점수가 하락하여 불량 라벨을 사전 경고할 수 있습니다.");

    public override string FormatParameterSummary(InspectionSuiteParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        Barcode1DOptions opt = parameters.Barcode;
        return $"Code 128B 다중 스캔라인={opt.ScanlineCount}줄 · 최소 심볼 대비(SC)={opt.MinSymbolContrast:F2} · Modulo-103 검증";
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
        Barcode1DOptions raw = context.Parameters.Barcode;
        return ExecuteMany(context, CropMarginPixels, raw, EvaluateTargetMany);
    }

    private static void EvaluateTargetMany(
        RoiInspectionContext context,
        InspectionSuiteRoiInput target,
        LocalRoiCrop localCrop,
        Barcode1DOptions rawOptions,
        Func<double> measureElapsedMicroseconds,
        ImmutableArray<InspectionSuiteItemResult>.Builder results)
    {
        int scanlineCount = Math.Clamp(rawOptions.ScanlineCount, Barcode1DOptions.MinScanlineCount, Barcode1DOptions.MaxScanlineCount);
        double minSymbolContrast = Math.Clamp((double)rawOptions.MinSymbolContrast, Barcode1DOptions.MinSymbolContrastBound, Barcode1DOptions.MaxSymbolContrastBound);

        Barcode1DDecodeResult[] decodedItems = Barcode1DDecoder.DecodeAllMultiScanline(
            localCrop.Crop.Pixels.Span,
            localCrop.Crop.Width,
            localCrop.Crop.Height,
            localCrop.Crop.Stride,
            scanlineCount: scanlineCount);

        double elapsedUs = measureElapsedMicroseconds();
        if (decodedItems.Length == 0)
        {
            var failBuilder = new InspectionItemResultBuilder(target);
            results.Add(failBuilder.Build(
                isPass: false,
                badgeLabel: "1D 바코드 판독 실패",
                detailLine: "유효한 Code 128 / EAN-13 바코드 패턴 또는 체크섬을 복원하지 못했습니다.",
                elapsedMicroseconds: elapsedUs));
            return;
        }

        double perItemUs = elapsedUs / decodedItems.Length;
        for (int idx = 0; idx < decodedItems.Length; idx++)
        {
            Barcode1DDecodeResult decoded = decodedItems[idx];
            string itemName = decodedItems.Length == 1 ? target.Name : $"{target.Name} #{idx + 1}";

            double worldCx = decoded.CenterX + localCrop.OffsetX;
            double worldCy = decoded.CenterY + localCrop.OffsetY;
            double barW = decoded.WidthPixels;
            double barH = decoded.HeightPixels;
            double hw = barW * HalfFactor;
            double hh = barH * HalfFactor;
            var referencePoint = new PointD(worldCx, worldCy);
            double referenceAngle = decoded.AngleRad;
            double angleDeg = decoded.AngleDeg;

            (double sinA, double cosA) = Math.SinCos(referenceAngle);
            double ux = hw * cosA;
            double uy = hw * sinA;
            double vx = -hh * sinA;
            double vy = hh * cosA;

            var tl = new PointD(worldCx - ux - vx, worldCy - uy - vy);
            var tr = new PointD(worldCx + ux - vx, worldCy + uy - vy);
            var br = new PointD(worldCx + ux + vx, worldCy + uy + vy);
            var bl = new PointD(worldCx - ux + vx, worldCy - uy + vy);
            ImmutableArray<PointD> symbolBounds = [tl, tr, br, bl];

            double worldLeft = Math.Min(Math.Min(tl.X, tr.X), Math.Min(br.X, bl.X));
            double worldRight = Math.Max(Math.Max(tl.X, tr.X), Math.Max(br.X, bl.X));
            double worldTop = Math.Min(Math.Min(tl.Y, tr.Y), Math.Min(br.Y, bl.Y));
            double worldBottom = Math.Max(Math.Max(tl.Y, tr.Y), Math.Max(br.Y, bl.Y));

            var scanStart = new PointD(worldCx - ux, worldCy - uy);
            var scanEnd = new PointD(worldCx + ux, worldCy + uy);
            double wingBack = Math.Max(MinWingBackPx, hw * WingBackHalfWidthRatio);
            double wingSide = Math.Max(MinWingSidePx, hw * WingSideHalfWidthRatio);
            var wing1 = new PointD(scanEnd.X - (wingBack * cosA) + (wingSide * sinA), scanEnd.Y - (wingBack * sinA) - (wingSide * cosA));
            var wing2 = new PointD(scanEnd.X - (wingBack * cosA) - (wingSide * sinA), scanEnd.Y - (wingBack * sinA) + (wingSide * cosA));

            bool isSingleSymbol = decodedItems.Length == 1;
            var builder = isSingleSymbol
                ? new InspectionItemResultBuilder(target).WithPose(referencePoint, referenceAngle)
                : new InspectionItemResultBuilder(
                    target.RoiId,
                    itemName,
                    referencePoint,
                    referenceAngle,
                    symbolBounds);
            builder.AddPrimaryPoint(referencePoint);
            builder.AddSecondaryPoint(tl);
            builder.AddSecondaryPoint(tr);
            builder.AddSecondaryPoint(br);
            builder.AddSecondaryPoint(bl);
            if (isSingleSymbol)
            {
                builder.AddContour(symbolBounds);
            }

            builder.AddContour([scanStart, scanEnd, wing1, scanEnd, wing2]);

            bool isPass = decoded.IsDecoded && decoded.IsChecksumValid && decoded.SymbolContrast >= minSymbolContrast;
            string badge = $"'{decoded.Payload}' @ ({worldCx:F1}, {worldCy:F1}) · θ={angleDeg:F2}° [{decoded.Symbology} · {barW:F0}×{barH:F0}px] · ISO={decoded.IsoGradeLetter}";
            string detail = $"1D 바코드({decoded.Symbology}) 판독='{decoded.Payload}', 바코드 중심좌표=({worldCx:F1}, {worldCy:F1}), 회전각={angleDeg:F2}°, 바운딩박스=[좌:{worldLeft:F1}, 상:{worldTop:F1} ~ 우:{worldRight:F1}, 하:{worldBottom:F1}] (크기={barW:F1}×{barH:F1}px, 각도={angleDeg:F2}°), 합격={isPass}, 체크섬={decoded.ChecksumValue}({(decoded.IsChecksumValid ? "정상" : "오류")}), 유효스캔선={decoded.DecodedScanlineCount}/{scanlineCount}, ISO/IEC 15416 등급={decoded.IsoGradeLetter} (대비 SC={decoded.SymbolContrast:P0}≥{minSymbolContrast:P0}, 변조 MOD={decoded.Modulation:P0}, 디코더빌리티 DEC={decoded.Decodability:P0})";

            results.Add(builder.Build(isPass, badge, detail, perItemUs));
        }
    }
}
