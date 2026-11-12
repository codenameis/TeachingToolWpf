using System.Collections.Immutable;
using VisionAlgorithm.OpenSrc.Common;
using VisionAlgorithm.OpenSrc.DataCode2D;
using VisionGeometry;

namespace TeachingToolWpf.Imaging.Matching;

/// <summary>
/// 21번 산업용 ISO/IEC 18004 2D QR Code 리더 (360° 정밀 회전각·월드 중심좌표·원근 호모그래피·GF(256) Reed-Solomon 오류 정정·ISO/IEC 15415 등급).
/// </summary>
public sealed class QrCode2DRoiAlgorithm : RoiAlgorithmBase
{
    private const int CropMarginPixels = 16;
    private const double HalfFactor = 0.5;
    private const double RadiansToDegrees = 180.0 / Math.PI;
    private const double MinArrowLengthPx = 16.0;
    private const double ArrowLengthHalfWidthRatio = 0.85;
    private const double MinArrowWingBackPx = 5.5;
    private const double ArrowWingBackRatio = 0.25;
    private const double MinArrowWingSidePx = 4.5;
    private const double ArrowWingSideRatio = 0.18;

    public override InspectionSuiteAlgorithmKind Kind => InspectionSuiteAlgorithmKind.QrCode2DReader;

    public override InspectionSuiteCategory Category => InspectionSuiteCategory.Barcode1D2D;

    public override string ShortName => "21. [본검사] 산업용 2D QR 코드(ISO 18004) 리더 · 360° 각도/위치 & ISO 15415 등급";

    public override string ShortPillLabel => "2D QR 코드";

    public override string AssignmentBadge => "스위트 · QR 코드";

    public override string Description =>
        "ISO/IEC 18004 2D QR 코드 리더: 3개 7×7 Finder(1:1:3:1:1) 서브픽셀 정합 및 Alignment 원근 호모그래피 복원, 전방위 360°(-180°~+180°) 회전각·월드 중심좌표 산출, GF(2^8)(p=0x11D) Reed-Solomon 오류 정정 및 ISO/IEC 15415 품질 등급(A~F)을 판정합니다.";

    public override string UsageGuide =>
        "① 2D QR 코드(주변 여백 Quiet Zone 포함)를 감싸도록 ROI를 그리세요 (하나의 ROI 안 여러 QR 코드 동시 판독 지원).\n② 3-Finder 직각이등변 삼각 정합으로 각 심볼의 정확한 월드 중심좌표(X, Y)와 360° 절대 회전각(θ), 디코딩 문자열, Reed-Solomon 복원 바이트 수, ISO/IEC 15415 등급을 모두 출력합니다.";

    public override string SampleSceneSlug => "qrcode";

    public override string SampleSceneTitle => "21. [본검사] 산업용 2D QR 코드(ISO 18004) 360° 위치·각도 리더 샘플 (1200×800)";

    public override InspectionAlgorithmHelpSpec HelpSpec { get; } = new(
        "[2D 코드] ISO/IEC 18004 QR 코드 리더 · 360° 위치/회전각 정합 & Reed-Solomon 복호",
        "1:1:3:1:1 3-Finder 서브픽셀 중심 · 8-DOF 원근 호모그래피 · BCH(15,5) 포맷 · GF(2^8)(p=0x11D) 오류 정정",
        "• 수식 원리:\n" +
        "  1) 3-Finder 서브픽셀 위치·각도 정합: 수평·수직·대각 1:1:3:1:1 런 비율 탐색 후 내부 3×3 코어 원판(r ≤ 2.15p) 연속 명암 무게중심으로 Top-Left / Top-Right / Bottom-Left 파인더 중심을 서브픽셀 정밀도로 구합니다. 외적 부호로 -180°~+180° 전방위 절대 회전각 θ와 월드 중심(X, Y)을 모호성 없이 복원합니다.\n" +
        "  2) 원근 호모그래피 & BCH/Reed-Solomon 복호: 우하단 5×5 Alignment 패턴까지 포함한 4점 호모그래피 H를 풀어 모듈 격자를 샘플링하고, BCH(15,5) 포맷 정보(최대 3비트 정정)와 8종 마스크 해제 후 GF(2^8)(x^8+x^4+x^3+x^2+1=0x11D) Berlekamp-Massey / Chien / Forney 알고리즘으로 스크래치 손상 바이트를 복원합니다.\n" +
        "  3) ISO/IEC 15415 인쇄 품질 등급: 심볼 대비, 모듈 변조도, 고정 패턴 건전성, 축 비균일도, 미사용 오류 정정 여유(UEC)로 Grade A~F를 산정합니다.",
        "① 라벨, PCB, 부품 표면에 인쇄/각인된 QR 코드 전체를 포함하도록 ROI를 배치합니다.\n" +
        "② [선택 ROI 실행]을 누르면 디코딩 문자열, 월드 중심좌표(X, Y), 360° 회전각(θ), 4모서리 외곽선과 방향 화살표가 즉시 표시됩니다.",
        "• 0°~360° 임의 회전과 명암 반전(흰 배경/검은 인쇄 및 어두운 기판/밝은 레이저 각인)을 별도 설정 없이 자동 인식합니다.",
        "• 코드 일부가 오염·스크래치로 훼손되어도 ECC(L/M/Q/H) 허용 범위 내라면 원본 문자열을 복원하고 정정된 바이트 수를 상세 리포트에 표시합니다.");

    public override string FormatParameterSummary(InspectionSuiteParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        QrCodeOptions opt = parameters.QrCode;
        return $"QR(ISO 18004) 최소 ISO 대비={opt.MinIsoContrast:F2} · RS(0x11D) 최대 허용 정정={opt.MaxEccErrors}개 · 360° 위치/각도";
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
        QrCodeOptions validated = context.Parameters.QrCode.Validate();
        return ExecuteMany(context, CropMarginPixels, validated, EvaluateTargetMany);
    }

    private static void EvaluateTargetMany(
        RoiInspectionContext context,
        InspectionSuiteRoiInput target,
        LocalRoiCrop localCrop,
        QrCodeOptions options,
        Func<double> measureElapsedMicroseconds,
        ImmutableArray<InspectionSuiteItemResult>.Builder results)
    {
        double minIsoContrast = Math.Clamp((double)options.MinIsoContrast, QrCodeOptions.MinIsoContrastBound, QrCodeOptions.MaxIsoContrastBound);
        int maxEccErrors = options.MaxEccErrors;

        var view = new ReadOnlyImageView2D<byte>(localCrop.Crop.Pixels.Span, localCrop.Crop.Width, localCrop.Crop.Height, localCrop.Crop.Stride);
        QrCodeDecodeResult[] decodedItems = QrCodeDecoder.DecodeAll(view);
        double elapsedUs = measureElapsedMicroseconds();

        if (decodedItems.Length == 0)
        {
            var failBuilder = new InspectionItemResultBuilder(target);
            results.Add(failBuilder.Build(
                isPass: false,
                badgeLabel: "QR 코드 판독 실패",
                detailLine: "유효한 ISO/IEC 18004 QR 코드 Finder 패턴(1:1:3:1:1) 또는 Reed-Solomon 부호어를 복원하지 못했습니다.",
                elapsedMicroseconds: elapsedUs));
            return;
        }

        double perItemUs = elapsedUs / decodedItems.Length;
        double ox = localCrop.OffsetX;
        double oy = localCrop.OffsetY;

        for (int idx = 0; idx < decodedItems.Length; idx++)
        {
            QrCodeDecodeResult decoded = decodedItems[idx];
            string itemName = decodedItems.Length == 1 ? target.Name : $"{target.Name} #{idx + 1}";

            double worldCx = decoded.CenterWorld.X + ox;
            double worldCy = decoded.CenterWorld.Y + oy;
            double symW = decoded.WidthPixels;
            double symH = decoded.HeightPixels;
            double referenceAngle = decoded.AngleRad;
            double angleDeg = referenceAngle * RadiansToDegrees;

            var referencePoint = new PointD(worldCx, worldCy);

            var tl = new PointD(decoded.TopLeftCorner.X + ox, decoded.TopLeftCorner.Y + oy);
            var tr = new PointD(decoded.TopRightCorner.X + ox, decoded.TopRightCorner.Y + oy);
            var br = new PointD(decoded.BottomRightCorner.X + ox, decoded.BottomRightCorner.Y + oy);
            var bl = new PointD(decoded.BottomLeftCorner.X + ox, decoded.BottomLeftCorner.Y + oy);
            ImmutableArray<PointD> symbolBounds = [tl, tr, br, bl];

            var ftl = new PointD(decoded.TopLeftFinderCenter.X + ox, decoded.TopLeftFinderCenter.Y + oy);
            var ftr = new PointD(decoded.TopRightFinderCenter.X + ox, decoded.TopRightFinderCenter.Y + oy);
            var fbl = new PointD(decoded.BottomLeftFinderCenter.X + ox, decoded.BottomLeftFinderCenter.Y + oy);

            double worldLeft = Math.Min(Math.Min(tl.X, tr.X), Math.Min(br.X, bl.X));
            double worldRight = Math.Max(Math.Max(tl.X, tr.X), Math.Max(br.X, bl.X));
            double worldTop = Math.Min(Math.Min(tl.Y, tr.Y), Math.Min(br.Y, bl.Y));
            double worldBottom = Math.Max(Math.Max(tl.Y, tr.Y), Math.Max(br.Y, bl.Y));

            (double sinA, double cosA) = Math.SinCos(referenceAngle);
            double hw = symW * HalfFactor;
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
            builder.AddSecondaryPoint(ftl);
            builder.AddSecondaryPoint(ftr);
            builder.AddSecondaryPoint(fbl);
            if (isSingleSymbol)
            {
                builder.AddContour(symbolBounds);
            }

            builder.AddContour([referencePoint, arrowTip, wing1, arrowTip, wing2]);

            bool isPass = decoded.IsDecoded
                && decoded.SymbolContrast >= minIsoContrast
                && decoded.CorrectedByteErrors <= maxEccErrors;

            string badge = $"'{decoded.DecodedText}' @ ({worldCx:F1}, {worldCy:F1}) · θ={angleDeg:F2}° [V{decoded.Version} {decoded.Dimension}×{decoded.Dimension} · ECC-{decoded.ErrorCorrectionLevel}] · ISO={decoded.OverallIsoGrade}";
            string detail = $"QR 코드 판독='{decoded.DecodedText}' (V{decoded.Version} · {decoded.Dimension}×{decoded.Dimension} 모듈 · ECC-{decoded.ErrorCorrectionLevel} · Mask={decoded.MaskPattern}), 중심좌표=({worldCx:F1}, {worldCy:F1}), 회전각={angleDeg:F2}°, 바운딩박스=[좌:{worldLeft:F1}, 상:{worldTop:F1} ~ 우:{worldRight:F1}, 하:{worldBottom:F1}] (크기={symW:F1}×{symH:F1}px, 각도={angleDeg:F2}°), 합격={isPass}, ISO/IEC 15415 등급={decoded.OverallIsoGrade} (대비={decoded.SymbolContrast:P0}≥{minIsoContrast:P0}, 변조={decoded.ModulationScore:P0}, 고정패턴={decoded.FixedPatternDamageScore:P0}, 비균일도={decoded.AxialNonUniformity:F3}, Reed-Solomon 복원 오류={decoded.CorrectedByteErrors}≤{maxEccErrors}바이트)";

            results.Add(builder.Build(isPass, badge, detail, perItemUs));
        }
    }
}
