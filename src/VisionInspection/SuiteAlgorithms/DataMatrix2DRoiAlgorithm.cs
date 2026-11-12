using System.Collections.Immutable;
using VisionAlgorithm.OpenSrc.DataCode2D;
using VisionAlgorithm.OpenSrc.Common;
using VisionGeometry;

namespace TeachingToolWpf.Imaging.Matching;

/// <summary>
/// 16번 산업용 ISO/IEC 16022 ECC 200 2D DataMatrix 리더 및 Reed-Solomon 오류 정정 알고리즘.
/// </summary>
public sealed class DataMatrix2DRoiAlgorithm : RoiAlgorithmBase
{
    private const int CropMarginPixels = 12;
    private const double HalfFactor = 0.5;
    private const double RadiansToDegrees = 180.0 / Math.PI;
    private const double MinArrowLengthPx = 14.0;
    private const double ArrowLengthHalfWidthRatio = 0.85;
    private const double MinArrowWingBackPx = 5.0;
    private const double ArrowWingBackRatio = 0.25;
    private const double MinArrowWingSidePx = 4.0;
    private const double ArrowWingSideRatio = 0.18;

    public override InspectionSuiteAlgorithmKind Kind => InspectionSuiteAlgorithmKind.DataMatrix2DReader;

    public override InspectionSuiteCategory Category => InspectionSuiteCategory.Barcode1D2D;

    public override string ShortName => "16. [본검사] 산업용 2D DataMatrix ECC 200 리더 · ISO/IEC 15415 품질 등급";

    public override string ShortPillLabel => "2D DataMatrix";

    public override string AssignmentBadge => "스위트 · DataMatrix";

    public override string Description =>
        "ISO/IEC 16022 ECC 200 2D DataMatrix 리더: L-Finder 및 교번 타이밍 트랙 정합, Utah 8-bit L자 모듈 배치 복원, GF(2^8)(원시 다항식 301) Reed-Solomon 신드롬 검증·오류 정정, ISO/IEC 15415 인쇄 품질 등급(A~F)을 산출합니다.";

    public override string UsageGuide =>
        "① 2D DataMatrix(ECC 200) 심볼이 포함되도록 ROI를 그리세요 (하나의 ROI 안 여러 심볼 동시 판독 지원).\n② L-Finder 및 타이밍 보더 검출 → Utah 모듈 추출 → GF(256) Reed-Solomon 오류 정정 → 각 심볼별 위치(X, Y)·회전각(θ)·문자열과 ISO/IEC 15415 품질 등급(A~F)을 모두 판독합니다.";

    public override string SampleSceneSlug => "datamatrix";

    public override string SampleSceneTitle => "16. [본검사] 산업용 2D DataMatrix ECC 200 리더 샘플 (1200×800)";

    public override InspectionAlgorithmHelpSpec HelpSpec { get; } = new(
        "[2D 코드] ISO/IEC 16022 ECC 200 DataMatrix 리더 & Reed-Solomon 오류 정정",
        "L-Finder & 교번 타이밍 트랙 정합 · Utah 8-Bit 모듈 매핑 · GF(2^8)(p=301) Berlekamp-Massey / Forney 복호",
        "• 수식 원리:\n" +
        "  1) 기하 정합: 실선 L-Finder 두 변과 반대편 흑백 교번 타이밍 파선(Timing Track)을 서브픽셀로 찾아 심볼 매트릭스 크기(10×10 ~ 26×26)와 아핀/원근 셀 중심 격자를 결정합니다.\n" +
        "  2) Utah 비트 배치 & GF(256) Reed-Solomon: ISO/IEC 16022 표준 Utah L자 트로미노 순서로 코드워드를 읽고, 원시 다항식 x^8 + x^5 + x^3 + x^2 + 1 (301) 기반 갈루아 체 GF(2^8)에서 신드롬 S_j 계산 → Berlekamp-Massey 오류 위치 다항식 Λ(x) → Chien Search → Forney 알고리즘으로 스크래치·오염으로 손상된 모듈 바이트를 수학적으로 완벽히 복원합니다.\n" +
        "  3) ISO/IEC 15415 품질 등급: 기호 대비(Symbol Contrast), 축 비균일성(Axial Non-Uniformity), 미사용 오류 정정 여유(UEC)를 종합해 Grade A~F를 판정합니다.",
        "① PCB, 메탈 부품, 라벨에 각인된 2D DataMatrix 코드 전체(주변 여백 Quiet Zone 1~2모듈 포함)를 감싸도록 ROI를 그립니다.\n" +
        "② [선택 ROI 실행]을 누르면 디코딩된 문자열, 정정된 오류 바이트 수, ISO 인쇄 품질 등급(Grade A~F), 모듈 그리드 오버레이가 즉시 표시됩니다.",
        "• 특별한 수치 튜닝 없이 자동 Otsu/적응형 대비 정합으로 작동하며, 회전·명암 반전(밝은 배경/어두운 코드 및 어두운 배경/밝은 레이저 각인)을 모두 지원합니다.",
        "• 코드 표면 일부가 스크래치로 훼손되어도 ECC 200 Reed-Solomon 정정 용량 범위 내라면 100% 원본 문자열로 복원되며, 정정된 바이트 수가 결과 상세에 기록됩니다.");

    public override string FormatParameterSummary(InspectionSuiteParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        DataMatrixOptions opt = parameters.DataMatrix;
        return $"ECC 200 최소 ISO 대비={opt.MinIsoContrast:F2} · Reed-Solomon 최대 정정 부호어={opt.MaxEccErrors}개 · GF(2^8) p=301";
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
        DataMatrixOptions validated = context.Parameters.DataMatrix.Validate();
        return ExecuteMany(context, CropMarginPixels, validated, EvaluateTargetMany);
    }

    private static void EvaluateTargetMany(
        RoiInspectionContext context,
        InspectionSuiteRoiInput target,
        LocalRoiCrop localCrop,
        DataMatrixOptions options,
        Func<double> measureElapsedMicroseconds,
        ImmutableArray<InspectionSuiteItemResult>.Builder results)
    {
        double minIsoContrast = Math.Clamp((double)options.MinIsoContrast, DataMatrixOptions.MinIsoContrastBound, DataMatrixOptions.MaxIsoContrastBound);
        int maxEccErrors = options.MaxEccErrors;

        var view = new ReadOnlyImageView2D<byte>(localCrop.Crop.Pixels.Span, localCrop.Crop.Width, localCrop.Crop.Height, localCrop.Crop.Stride);
        DataMatrixDecodeResult[] decodedItems = DataMatrixEcc200Decoder.DecodeAll(view);
        double elapsedUs = measureElapsedMicroseconds();

        if (decodedItems.Length == 0)
        {
            var failBuilder = new InspectionItemResultBuilder(target);
            results.Add(failBuilder.Build(
                isPass: false,
                badgeLabel: "DataMatrix 판독 실패",
                detailLine: "유효한 DataMatrix ECC 200 L-Finder 패턴 또는 Reed-Solomon 체크섬을 복원하지 못했습니다.",
                elapsedMicroseconds: elapsedUs));
            return;
        }

        double perItemUs = elapsedUs / decodedItems.Length;
        for (int idx = 0; idx < decodedItems.Length; idx++)
        {
            DataMatrixDecodeResult decoded = decodedItems[idx];
            string itemName = decodedItems.Length == 1 ? target.Name : $"{target.Name} #{idx + 1}";

            double worldCx = decoded.CenterWorld.X + localCrop.OffsetX;
            double worldCy = decoded.CenterWorld.Y + localCrop.OffsetY;
            double barW = decoded.WidthPixels;
            double barH = decoded.HeightPixels;
            double hw = barW * HalfFactor;
            double hh = barH * HalfFactor;
            var referencePoint = new PointD(worldCx, worldCy);
            double referenceAngle = decoded.AngleRad;
            double angleDeg = referenceAngle * RadiansToDegrees;

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

            double arrowLen = Math.Max(MinArrowLengthPx, hw * ArrowLengthHalfWidthRatio);
            double wingBack = Math.Max(MinArrowWingBackPx, arrowLen * ArrowWingBackRatio);
            double wingSide = Math.Max(MinArrowWingSidePx, arrowLen * ArrowWingSideRatio);
            var arrowTip = new PointD(worldCx + (arrowLen * cosA), worldCy + (arrowLen * sinA));
            var wing1 = new PointD(arrowTip.X - (wingBack * cosA) + (wingSide * sinA), arrowTip.Y - (wingBack * sinA) - (wingSide * cosA));
            var wing2 = new PointD(arrowTip.X - (wingBack * cosA) - (wingSide * sinA), arrowTip.Y - (wingBack * sinA) + (wingSide * cosA));

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

            builder.AddContour([referencePoint, arrowTip, wing1, arrowTip, wing2]);

            bool isPass = decoded.IsDecoded
                && decoded.SymbolContrast >= minIsoContrast
                && decoded.CorrectedByteErrors <= maxEccErrors;
            string badge = $"'{decoded.DecodedText}' @ ({worldCx:F1}, {worldCy:F1}) · θ={angleDeg:F2}° [{decoded.SymbolRows}×{decoded.SymbolCols} · {barW:F0}×{barH:F0}px] · ISO={decoded.OverallIsoGrade}";
            string detail = $"2D 바코드 판독='{decoded.DecodedText}' ({decoded.SymbolRows}×{decoded.SymbolCols} 모듈), 바코드 중심좌표=({worldCx:F1}, {worldCy:F1}), 회전각={angleDeg:F2}°, 바운딩박스=[좌:{worldLeft:F1}, 상:{worldTop:F1} ~ 우:{worldRight:F1}, 하:{worldBottom:F1}] (크기={barW:F1}×{barH:F1}px, 각도={angleDeg:F2}°), 합격={isPass}, ISO/IEC 15415 등급={decoded.OverallIsoGrade} (대비={decoded.SymbolContrast:P0}≥{minIsoContrast:P0}, 변조={decoded.ModulationScore:P0}, 고정패턴={decoded.FixedPatternDamageScore:P0}, 비균일도={decoded.AxialNonUniformity:F3}, Reed-Solomon 복원 오류={decoded.CorrectedByteErrors}≤{maxEccErrors}바이트)";

            results.Add(builder.Build(isPass, badge, detail, perItemUs));
        }
    }
}
