using System.Buffers;
using System.Collections.Immutable;
using System.Diagnostics;
using VisionAlgorithm.OpenSrc.Common;
using VisionAlgorithm.OpenSrc.IndustrialOcrOcv;
using VisionGeometry;
using VisionOcr.PpOcr;

namespace TeachingToolWpf.Imaging.Matching;

/// <summary>
/// 9번 통합 OCR / OCV (PP-OCRv4 ONNX 범용 판독 · 티칭 OCV 품질 검증) 알고리즘.
/// </summary>
public sealed class PpOcrRoiAlgorithm : RoiAlgorithmBase
{
    private const int MaxOcrRectifiedWidth = 4096;
    private const int MaxOcrRectifiedHeight = 2048;
    private const int OcrCropMarginPixels = 16;
    private const int DefaultOcrPadX = 6;
    private const int DefaultOcrPadY = 4;
    private const int MinRectifiedOcrWidth = 12;
    private const int MinRectifiedOcrHeight = 10;
    private const int QuadPolygonVertexCount = 4;
    private const float HalfCenterFactor = 0.5f;
    private const double MinGuideHalfWidthPx = 12.0;
    private const double MinGuideHalfHeightPx = 6.0;
    private const double GuideInnerWidthRatio = 0.47;
    private const double GuideInnerHeightRatio = 0.40;

    private readonly record struct RectifiedOcrSlot(
        InspectionSuiteRoiInput Roi,
        FixturePose2D Fixture,
        int OffsetX,
        int OffsetY,
        int RoiWidth,
        int RoiHeight,
        byte[] RentedPixels,
        double PrepMicroseconds);

    public override InspectionSuiteAlgorithmKind Kind => InspectionSuiteAlgorithmKind.PpOcrOnnx;

    public override InspectionSuiteCategory Category => InspectionSuiteCategory.Ocr;

    public override string ShortName => "9. OCR / OCV (PP-OCRv4 범용 판독 · 티칭 OCV 품질 검증)";

    public override string ShortPillLabel => "OCR / OCV (PP-OCRv4 · 티칭 OCV)";

    public override string AssignmentBadge => "스위트 · OCR/OCV";

    public override string Description =>
        "PP-OCRv4 ONNX(DBNet 검출 + SVTR-LCNet CTC 시퀀스 배치 인식) 범용 문자 판독과 동일 인쇄 골든 폰트 기반 고속 OCV(Optical Character Verification) 품질 검증을 통합 제공합니다.";

    public override string UsageGuide =>
        "① [OCR 문자 판독] 인쇄·레이저 각인·도트매트릭스·영수증 문자열 위에 ROI를 배치하고 운전 프리셋(범용 자동 / 고속 양산)과 허용 문자 집합을 선택하세요.\n② [OCV 품질 검증] 'PP-OCR 판독값 → 기대 문자열 채우기' 또는 직접 기대 문자열을 입력한 뒤 '선택 ROI 폰트 티칭' → 'OCV 검증'으로 인쇄 결손·번짐·오인쇄를 초고속 검증합니다.";

    public override string SampleSceneSlug => "ocrocv";

    public override string SampleSceneTitle => "9. 통합 OCR / OCV (PP-OCRv4 범용 판독 · 티칭 OCV 품질 검증) 샘플 (1200×800)";

    public override bool SuppressesGuideOverlayWhenEvaluated => true;

    public override InspectionAlgorithmHelpSpec HelpSpec { get; } = new(
        "통합 OCR / OCV (PP-OCRv4 ONNX 범용 문자 판독 · 티칭 기반 OCV 인쇄 품질 검증)",
        "PP-OCRv4 DBNet(det) + SVTR-LCNet CTC(rec) 동일 폭 버킷 배치 추론 · 티칭 골든 폰트 OCV 검증",
        "• OCR(범용 문자 판독): 단일 줄 ROI는 H=48 비율 유지 리사이즈 + SIMD 평면 정규화 및 동일 목표 폭 버킷 다중 ROI 일괄(Batch) 추론으로 SVTR-LCNet CTC 첫 타임스텝 확률을 디코딩하며, 다중 줄·회전·투시 ROI는 DBNet 검출 후 폴리곤 정류 일괄 인식을 수행합니다.\n" +
        "• OCV(인쇄 품질 검증): 동일 인쇄 공정의 기준 ROI에서 골든 폰트(폰트 ID별 다중 등록 지원)를 티칭한 뒤 글자별 획 결손·번짐·동형 문자 치환·누락을 검증하며 OK / REVIEW(경계 경고) / NG(제품 불량)를 분리합니다.",
        "① 범용 문자 판독(OCR): 텍스트 영역에 ROI를 지정하고 운전 프리셋(범용 자동 / 고속 양산)·언어·극성을 선택한 뒤 [선택/전체 ROI 실행]을 누릅니다.\n" +
        "② 인쇄 품질 검증(OCV): 단일 ROI를 선택해 기대 문자열을 입력(또는 PASS 판독값 확인 복사)하고 기준 ROI에서 [폰트 티칭] 후 [선택/전체 ROI OCV 검증]을 실행합니다.",
        "• 필드 형식 마스크 문법: D=숫자(0-9), A=영문 대문자, a=영문 소문자, X=영숫자, #=공백 제외 임의 문자, [0-9A-F]=명시 범위, \\문자=리터럴 (정규식은 지원하지 않으며 예: AAAADDDD, LOT\\-DDDD\\-ADD).\n" +
        "• 실행 장치 안내: 단일 소형 ROI에서는 CPU 실행을 권장하며 CUDA는 실험용(대규모 다중 ROI 배치 조건)입니다.\n" +
        "• 양산 무결점 검증 주의: 무티칭 PP-OCR 단독 PASS/NG 판정만으로는 동형 문자 오인식을 완전히 배제할 수 없으므로 양산 불량 판정 시 반드시 티칭 OCV(TaughtFontOcvVerifier) 또는 필드 형식 마스크(OcrFieldFormatSpec)와 함께 운용하세요.",
        "• 허용 문자 집합(AllowedCharacterSet)은 CTC 디코딩 토큰 마스크로만 동작하며 결과 치환을 수행하지 않습니다.");

    public override string FormatParameterSummary(InspectionSuiteParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        PpOcrSuiteOptions opt = parameters.PpOcr;
        string presetLabel = opt.Preset == PpOcrSpeedPreset.HighSpeedProduction ? "고속 양산" : "범용 자동";
        return $"PP-OCRv4 ONNX ({presetLabel}) · 언어={opt.LanguageMode} · 극성={opt.Polarity} · 파이프라인={opt.Pipeline} · 신뢰도≥{opt.MinConfidence:F2} · 문자집합={OcrCharacterSetFilter.GetDisplayName(opt.AllowedCharacters)}";
    }

    public override ImmutableArray<ImmutableArray<PointD>> BuildGeometricGuides(
        InspectionSuiteRoiInput target,
        InspectionSuiteParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(parameters);
        return BuildStandardFramedScanGuides(
            target,
            MinGuideHalfWidthPx,
            MinGuideHalfHeightPx,
            GuideInnerWidthRatio,
            GuideInnerHeightRatio);
    }

    public override ImmutableArray<InspectionSuiteItemResult> Execute(RoiInspectionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        IReadOnlyList<InspectionSuiteRoiInput> rois = context.TargetRois;
        var results = ImmutableArray.CreateBuilder<InspectionSuiteItemResult>(rois.Count);

        if (context.PpOcrService is null)
        {
            foreach (InspectionSuiteRoiInput roi in rois)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                var unavailableBuilder = new InspectionItemResultBuilder(roi)
                    .AddDefectPoint(roi.Center)
                    .WithGuideContours(BuildGeometricGuides(roi, context.Parameters));
                results.Add(unavailableBuilder.Build(
                    isPass: false,
                    badgeLabel: "모델 미연결",
                    detailLine: "PP-OCR (ONNX) 서비스가 연결되지 않았습니다.",
                    elapsedMicroseconds: 0.0));
            }

            return results.ToImmutable();
        }

        PpOcrSuiteOptions opt = context.Parameters.PpOcr.Validate();
        PpOcrRunOptions runOptions = opt.Preset == PpOcrSpeedPreset.HighSpeedProduction
            ? PpOcrRunOptions.CreateHighSpeedProduction(
                language: opt.LanguageMode,
                polarityMode: opt.Polarity,
                allowedCharacterSet: opt.AllowedCharacters,
                minAcceptConfidence: opt.MinConfidence,
                minCharacterConfidence: opt.MinCharacterConfidence)
            : new PpOcrRunOptions(
                Language: opt.LanguageMode,
                PipelineMode: opt.Pipeline,
                PolarityMode: opt.Polarity,
                AllowedCharacterSet: opt.AllowedCharacters,
                MinAcceptConfidence: opt.MinConfidence,
                MinCharacterConfidence: opt.MinCharacterConfidence,
                FastInteractivePreview: opt.IsInteractivePreview);

        using var visionWs = new VisionWorkspace();
        var slots = new List<RectifiedOcrSlot>(rois.Count);

        try
        {
            var patches = new PpOcrRoiPatch[rois.Count];
            for (int i = 0; i < rois.Count; i++)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                InspectionSuiteRoiInput roi = rois[i];
                var prepSw = Stopwatch.StartNew();
                LocalRoiCrop localCrop = ReadLocalCrop(context.Reader, context.ImageWidth, context.ImageHeight, roi, OcrCropMarginPixels, context.CancellationToken);
                var cropView = new ReadOnlyImageView2D<byte>(localCrop.Crop.Pixels.Span, localCrop.Crop.Width, localCrop.Crop.Height, localCrop.Crop.Stride);
                var fixture = new FixturePose2D(localCrop.LocalCenter.X, localCrop.LocalCenter.Y, (float)roi.AngleRadians);

                int roiW = Math.Clamp((int)Math.Round(roi.Width) + DefaultOcrPadX, MinRectifiedOcrWidth, Math.Min(MaxOcrRectifiedWidth, cropView.Width));
                int roiH = Math.Clamp((int)Math.Round(roi.Height) + DefaultOcrPadY, MinRectifiedOcrHeight, Math.Min(MaxOcrRectifiedHeight, cropView.Height));
                int pixelCount = checked(roiW * roiH);

                byte[] rented = ArrayPool<byte>.Shared.Rent(pixelCount);
                Span<byte> rectifiedRoi = rented.AsSpan(0, pixelCount);
                Span<float> warpScratch = visionWs.GetFloatBufferA(pixelCount);
                SimdAffineWarper.WarpAffineBilinear(cropView, in fixture, roiW, roiH, rectifiedRoi, warpScratch);
                prepSw.Stop();

                slots.Add(new RectifiedOcrSlot(roi, fixture, localCrop.OffsetX, localCrop.OffsetY, roiW, roiH, rented, prepSw.Elapsed.TotalMicroseconds));
                patches[i] = new PpOcrRoiPatch(rented.AsMemory(0, pixelCount), roiW, roiH, roiW);
            }

            context.CancellationToken.ThrowIfCancellationRequested();
            var batchSw = Stopwatch.StartNew();
            IReadOnlyList<PpOcrRecognitionResult> batchOutputs = context.PpOcrService.RecognizeBatch(patches, runOptions);
            batchSw.Stop();

            double amortizedBatchMicroseconds = batchOutputs.Count > 0
                ? batchSw.Elapsed.TotalMicroseconds / batchOutputs.Count
                : 0.0;

            for (int i = 0; i < slots.Count; i++)
            {
                RectifiedOcrSlot slot = slots[i];
                InspectionSuiteRoiInput roi = slot.Roi;
                PpOcrRecognitionResult ppRes = batchOutputs[i];
                double itemElapsedUs = slot.PrepMicroseconds + amortizedBatchMicroseconds;

                if (ppRes.ModelStatus != PpOcrModelStatus.Ready)
                {
                    var missingBuilder = new InspectionItemResultBuilder(roi)
                        .AddDefectPoint(roi.Center)
                        .WithGuideContours(BuildGeometricGuides(roi, context.Parameters));
                    results.Add(missingBuilder.Build(
                        isPass: false,
                        badgeLabel: "모델 미설치",
                        detailLine: $"{ppRes.StatusMessage} (가짜 판독 결과 미반환)",
                        elapsedMicroseconds: itemElapsedUs));
                    continue;
                }

                var roiView = new ReadOnlyImageView2D<byte>(
                    slot.RentedPixels.AsSpan(0, slot.RoiWidth * slot.RoiHeight),
                    slot.RoiWidth,
                    slot.RoiHeight,
                    slot.RoiWidth);

                PpOcrRegionResult ocrRes = ppRes.Region;
                float halfW = (slot.RoiWidth - 1) * HalfCenterFactor;
                float halfH = (slot.RoiHeight - 1) * HalfCenterFactor;
                var builder = new InspectionItemResultBuilder(roi)
                    .AddPrimaryPoint(roi.Center)
                    .WithGuideContours([]);

                foreach (PpOcrBoxResult detBox in ppRes.Boxes)
                {
                    if (detBox.Polygon.Length == QuadPolygonVertexCount)
                    {
                        var worldPoly = ImmutableArray.CreateBuilder<PointD>(QuadPolygonVertexCount);
                        for (int p = 0; p < QuadPolygonVertexCount; p++)
                        {
                            var wp = slot.Fixture.LocalToWorld((float)detBox.Polygon[p].X - halfW, (float)detBox.Polygon[p].Y - halfH);
                            worldPoly.Add(new PointD(slot.OffsetX + wp.X, slot.OffsetY + wp.Y));
                        }

                        builder.AddContour(worldPoly.MoveToImmutable());
                    }
                }

                foreach (PpOcrCharacterBox ch in ocrRes.Characters)
                {
                    float lx0 = ch.StartCol - halfW;
                    float ly0 = ch.TopRow - halfH;
                    float lx1 = ch.EndCol - halfW;
                    float ly1 = ch.BottomRow - halfH;
                    var pc = slot.Fixture.LocalToWorld(HalfCenterFactor * (lx0 + lx1), HalfCenterFactor * (ly0 + ly1));
                    var worldPt = new PointD(slot.OffsetX + pc.X, slot.OffsetY + pc.Y);
                    if (ch.Confidence >= runOptions.MinCharacterConfidence && ch.Character != '?')
                    {
                        builder.AddPrimaryPoint(worldPt);
                    }
                    else
                    {
                        builder.AddDefectPoint(worldPt);
                    }
                }

                string singleLineDisplay = ocrRes.FullText.Replace("\n", " / ", StringComparison.Ordinal);
                string polarityTag = ppRes.UsedInvertedPolarity ? "LightOnDark(반전)" : "DarkOnLight(일반)";
                string stageTag = ppRes.UsedDetectionStage ? "Det+Rec" : "RecOnly";
                string batchTag = slots.Count > 1 ? $"·배치x{slots.Count}" : string.Empty;
                bool isPass = ocrRes.IsPass && !string.IsNullOrWhiteSpace(ocrRes.FullText) && builder.DefectPointCount == 0;

                string badge = string.IsNullOrWhiteSpace(singleLineDisplay)
                    ? "OCR N/A"
                    : $"'{singleLineDisplay}' · P={ocrRes.MeanConfidence:F2}";

                string detail = $"OCR({ppRes.ActiveDevice}·{stageTag}{batchTag})='{singleLineDisplay}', 합격={isPass}, 평균확률={ocrRes.MeanConfidence:F3}, 최소확률={ocrRes.MinConfidence:F3}, 줄수={ocrRes.Lines.Count}, 문자수={ocrRes.Characters.Count}개, 극성={polarityTag}";
                OcrDiagnosticSnapshot diag = OcrDiagnosticPreviewBuilder.Build(
                    roiView, ocrRes, InspectionSuiteAlgorithmKind.PpOcrOnnx, context.Parameters);
                builder.WithOcrDiagnostic(diag);

                results.Add(builder.Build(isPass, badge, detail, itemElapsedUs));
            }

            return results.ToImmutable();
        }
        finally
        {
            for (int i = 0; i < slots.Count; i++)
            {
                ArrayPool<byte>.Shared.Return(slots[i].RentedPixels);
            }
        }
    }
}
