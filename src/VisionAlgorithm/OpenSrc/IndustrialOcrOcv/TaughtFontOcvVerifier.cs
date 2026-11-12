using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using VisionAlgorithm.OpenSrc.Common;

namespace VisionAlgorithm.OpenSrc.IndustrialOcrOcv;

/// <summary>
/// Dedicated taught-font Optical Character Verification (OCV) verifier separated from general-purpose OCR.
/// Evaluates segmented glyphs against a user-taught <see cref="IndustrialOcrFontModel"/> under an optional
/// <see cref="OcrFieldFormatSpec"/> constraint using sequence alignment while strictly checking unconstrained
/// out-of-mask glyph matches so confusable misprints (e.g., <c>12B4</c> against <c>DDDD</c> / <c>1284</c>) are rejected.
/// </summary>
public sealed class TaughtFontOcvVerifier
{
    private const int MinRectifiedStripWidth = 16;
    private const int MinRectifiedStripHeight = 12;
    private const int LocalContrastContextRingMargin = 2;
    private const int StringBuilderExtraCapacity = 8;
    private const float SolidGlyphMarginContrastRatio = 2.5f;

    private readonly IndustrialOcrFontModel _goldenFont;

    public TaughtFontOcvVerifier(IndustrialOcrFontModel goldenFont)
    {
        ArgumentNullException.ThrowIfNull(goldenFont);
        _goldenFont = goldenFont;
    }

    public OcvVerificationResult VerifyLine(
        ReadOnlyImageView2D<byte> image,
        in FixturePose2D fixture,
        OcrFieldFormatSpec? fieldSpec,
        OcvVerificationOptions? options,
        VisionWorkspace workspace)
    {
        image.EnsureValid();
        fixture.EnsureValid();
        ArgumentNullException.ThrowIfNull(workspace);

        OcvVerificationOptions effectiveOptions = options ?? new OcvVerificationOptions();
        effectiveOptions.EnsureValid();
        OcrFieldFormatSpec effectiveSpec = fieldSpec ?? new OcrFieldFormatSpec();

        int stripW = Math.Max(
            MinRectifiedStripWidth,
            effectiveOptions.RectifiedWidth > 0 ? effectiveOptions.RectifiedWidth : image.Width);
        int stripH = Math.Max(
            MinRectifiedStripHeight,
            effectiveOptions.RectifiedHeight > 0 ? effectiveOptions.RectifiedHeight : image.Height);
        OcrMaxSegmentParams segParams = BuildSegmentParams(stripW, stripH, effectiveOptions);

        Span<byte> rectifiedStrip = workspace.GetByteBufferA(checked(stripW * stripH));
        int probeBufferLength = Math.Min(
            OcrMaxSegmenter.MaxSupportedSegmentedChars,
            effectiveOptions.MaxLineCharacters + 1);
        Span<SegmentedCharBox> boxes = stackalloc SegmentedCharBox[probeBufferLength];

        int boxCount = OcrMaxSegmenter.SegmentLine(
            image,
            in fixture,
            in segParams,
            workspace,
            rectifiedStrip,
            boxes);

        if (boxCount == 0)
        {
            float stripContrast = ComputePeakToPeakContrast(rectifiedStrip);
            return CreateFailureResult(
                effectiveSpec.ExpectedText,
                stripContrast,
                maxMissingRatio: 1f,
                causeSummary: "문자 분할 실패 (검출된 글자 0개)",
                recommendedAction: "인쇄 누락 여부를 확인하고 ROI 위치·명암 극성(DarkOnLight)·전경 대비 오프셋을 점검하세요.");
        }

        if (boxCount > effectiveOptions.MaxLineCharacters)
        {
            float stripContrast = ComputePeakToPeakContrast(rectifiedStrip);
            return CreateFailureResult(
                effectiveSpec.ExpectedText,
                stripContrast,
                maxMissingRatio: 0f,
                causeSummary: $"최대 허용 문자 수 초과 (검출 {boxCount}자 > 상한 {effectiveOptions.MaxLineCharacters}자 — 과분할 또는 노이즈 혼입)",
                recommendedAction: "배경 노이즈·조명 반사·전경 대비 오프셋을 점검하거나 필요한 경우 MaxLineCharacters 상한을 조정하세요.");
        }

        int gridLen = IndustrialOcrGlyphTemplate.GridLength;
        int quadLen = IndustrialOcrGlyphTemplate.QuadrantCount;
        int totalFeatureFloats = checked(boxCount * ((gridLen * 2) + quadLen));

        // [Perf Why]: Rent a single contiguous float buffer from ArrayPool instead of allocating 3 arrays per glyph box.
        float[] pooledFeatureBuffer = ArrayPool<float>.Shared.Rent(totalFeatureFloats);
        OcvSequenceAligner.ObservedBoxSummary[] pooledBoxes = ArrayPool<OcvSequenceAligner.ObservedBoxSummary>.Shared.Rent(boxCount);

        try
        {
            Span<float> allGrids = pooledFeatureBuffer.AsSpan(0, boxCount * gridLen);
            Span<float> allCl2ns = pooledFeatureBuffer.AsSpan(boxCount * gridLen, boxCount * gridLen);
            Span<float> allQuads = pooledFeatureBuffer.AsSpan(boxCount * gridLen * 2, boxCount * quadLen);
            Span<OcvSequenceAligner.ObservedBoxSummary> obsBoxes = pooledBoxes.AsSpan(0, boxCount);

            ExtractObservedBoxFeatures(
                rectifiedStrip,
                stripW,
                stripH,
                boxes.Slice(0, boxCount),
                effectiveOptions.DarkOnLight,
                allGrids,
                allCl2ns,
                allQuads,
                obsBoxes);

            return EvaluateAlignedLine(
                stripW,
                stripH,
                boxCount,
                obsBoxes,
                allGrids,
                allCl2ns,
                allQuads,
                effectiveSpec,
                effectiveOptions);
        }
        finally
        {
            ArrayPool<OcvSequenceAligner.ObservedBoxSummary>.Shared.Return(pooledBoxes, clearArray: true);
            ArrayPool<float>.Shared.Return(pooledFeatureBuffer);
        }
    }

    public static IReadOnlyList<GoldenCharSample> ExtractTeachingSamplesFromLine(
        ReadOnlyImageView2D<byte> image,
        in FixturePose2D fixture,
        string groundTruthText,
        OcvVerificationOptions? options,
        VisionWorkspace workspace)
    {
        image.EnsureValid();
        fixture.EnsureValid();
        ArgumentException.ThrowIfNullOrWhiteSpace(groundTruthText);
        ArgumentNullException.ThrowIfNull(workspace);

        OcvVerificationOptions effectiveOptions = options ?? new OcvVerificationOptions();
        effectiveOptions.EnsureValid();

        var compactChars = new List<char>(groundTruthText.Length);
        for (int i = 0; i < groundTruthText.Length; i++)
        {
            if (!char.IsWhiteSpace(groundTruthText[i]))
            {
                compactChars.Add(groundTruthText[i]);
            }
        }

        int stripW = Math.Max(
            MinRectifiedStripWidth,
            effectiveOptions.RectifiedWidth > 0 ? effectiveOptions.RectifiedWidth : image.Width);
        int stripH = Math.Max(
            MinRectifiedStripHeight,
            effectiveOptions.RectifiedHeight > 0 ? effectiveOptions.RectifiedHeight : image.Height);
        OcrMaxSegmentParams segParams = BuildSegmentParams(stripW, stripH, effectiveOptions);

        Span<byte> rectifiedStrip = workspace.GetByteBufferA(checked(stripW * stripH));
        int probeBufferLength = Math.Min(
            OcrMaxSegmenter.MaxSupportedSegmentedChars,
            effectiveOptions.MaxLineCharacters + 1);
        Span<SegmentedCharBox> boxes = stackalloc SegmentedCharBox[probeBufferLength];

        int boxCount = OcrMaxSegmenter.SegmentLine(
            image,
            in fixture,
            in segParams,
            workspace,
            rectifiedStrip,
            boxes);

        if (boxCount != compactChars.Count || boxCount > effectiveOptions.MaxLineCharacters)
        {
            return [];
        }

        var samples = new List<GoldenCharSample>(boxCount);
        for (int i = 0; i < boxCount; i++)
        {
            SegmentedCharBox box = boxes[i];
            (int cropX0, int cropY0, int cropW, int cropH, _) =
                ResolveGlyphFeatureWindow(rectifiedStrip, stripW, stripH, in box);

            byte[] patch = new byte[cropW * cropH];
            for (int r = 0; r < cropH; r++)
            {
                ReadOnlySpan<byte> srcRow = rectifiedStrip.Slice(((cropY0 + r) * stripW) + cropX0, cropW);
                srcRow.CopyTo(patch.AsSpan(r * cropW, cropW));
            }

            samples.Add(new GoldenCharSample(
                compactChars[i],
                patch,
                cropW,
                cropH,
                cropW,
                effectiveOptions.DarkOnLight));
        }

        return samples;
    }

    private void ExtractObservedBoxFeatures(
        ReadOnlySpan<byte> rectifiedStrip,
        int stripW,
        int stripH,
        ReadOnlySpan<SegmentedCharBox> boxes,
        bool darkOnLight,
        Span<float> allGrids,
        Span<float> allCl2ns,
        Span<float> allQuads,
        Span<OcvSequenceAligner.ObservedBoxSummary> obsBoxes)
    {
        Span<float> hProjScratch = stackalloc float[IndustrialOcrGlyphTemplate.GridHeight];
        Span<float> vProjScratch = stackalloc float[IndustrialOcrGlyphTemplate.GridWidth];
        int gridLen = IndustrialOcrGlyphTemplate.GridLength;
        int quadLen = IndustrialOcrGlyphTemplate.QuadrantCount;

        for (int j = 0; j < boxes.Length; j++)
        {
            SegmentedCharBox box = boxes[j];
            int featureOffset = j * gridLen;
            int quadOffset = j * quadLen;
            Span<float> grid = allGrids.Slice(featureOffset, gridLen);
            Span<float> cl2n = allCl2ns.Slice(featureOffset, gridLen);
            Span<float> quads = allQuads.Slice(quadOffset, quadLen);

            (int cropX0, int cropY0, int cropW, int cropH, float localContrastGv) =
                ResolveGlyphFeatureWindow(rectifiedStrip, stripW, stripH, in box);

            ReadOnlySpan<byte> patchSlice = rectifiedStrip.Slice((cropY0 * stripW) + cropX0);
            ReadOnlyImageView2D<byte> charView = new(patchSlice, cropW, cropH, stripW);

            IndustrialOcrTrainer.ExtractNormalizedGlyphFeatures(
                charView,
                darkOnLight,
                grid,
                hProjScratch,
                vProjScratch,
                quads,
                out float innerContrastGv);
            float contrastGv = MathF.Max(innerContrastGv, localContrastGv);

            SimpleShotNormalizer.CenterAndL2Normalize(grid, _goldenFont.GlobalMean, cl2n);
            OcvTemplateMatcher.TemplateMatchOutcome unc = OcvTemplateMatcher.MatchUnconstrained(_goldenFont, grid, cl2n, quads);

            obsBoxes[j] = new OcvSequenceAligner.ObservedBoxSummary(
                Box: box,
                FeatureOffset: featureOffset,
                QuadOffset: quadOffset,
                ContrastGv: contrastGv,
                UnconstrainedChar: unc.BestTemplate is not null ? unc.BestChar : OcvTemplateMatcher.UnmatchedRejectSymbol,
                UnconstrainedScore: unc.BestScore,
                UnconstrainedMargin: unc.FullTemplateConfusionMargin,
                UnconstrainedTemplate: unc.BestTemplate);
        }
    }

    private OcvVerificationResult EvaluateAlignedLine(
        int stripW,
        int stripH,
        int boxCount,
        ReadOnlySpan<OcvSequenceAligner.ObservedBoxSummary> obsBoxes,
        ReadOnlySpan<float> allGrids,
        ReadOnlySpan<float> allCl2ns,
        ReadOnlySpan<float> allQuads,
        OcrFieldFormatSpec effectiveSpec,
        OcvVerificationOptions effectiveOptions)
    {
        int targetSlotCount = !string.IsNullOrEmpty(effectiveSpec.CompactExpectedText)
            ? effectiveSpec.CompactExpectedText.Length
            : effectiveSpec.SlotCount;

        List<OcvSequenceAligner.AlignmentStep> steps = targetSlotCount > 0
            ? OcvSequenceAligner.AlignSlotsToObservedBoxes(
                _goldenFont,
                targetSlotCount,
                obsBoxes,
                allGrids,
                allCl2ns,
                allQuads,
                effectiveSpec,
                effectiveOptions)
            : OcvSequenceAligner.BuildUnalignedObservedSteps(boxCount);

        var charVerifications = new List<OcvCharacterVerification>(steps.Count);
        var decodedBuilder = new StringBuilder(boxCount + StringBuilderExtraCapacity);

        float sumMatchScore = 0f;
        int scoredCount = 0;
        float minMatchScore = 1f;
        float minContrastGv = float.MaxValue;
        float maxMissingRatio = 0f;
        float maxBleedRatio = 0f;
        bool hasAnyNg = false;
        bool hasAnyReview = false;
        string? firstNgCause = null;
        string? firstReviewCause = null;

        for (int s = 0; s < steps.Count; s++)
        {
            OcvSequenceAligner.AlignmentStep step = steps[s];
            if (step.Kind == OcvSequenceAligner.AlignmentStepKind.Deletion)
            {
                int slotIdx = step.SlotIndex;
                char? expectedChar = effectiveSpec.GetExpectedCharacterAt(slotIdx);
                string missingLabel = expectedChar.HasValue ? $"'{expectedChar.Value}'" : $"슬롯 #{slotIdx + 1}";
                string delCause = $"삭제(누락): 기대 문자 {missingLabel} 미검출";

                int anchorCol = OcvSequenceAligner.EstimateDeletedSlotColumn(steps, s, obsBoxes, stripW);
                charVerifications.Add(new OcvCharacterVerification(
                    CharIndex: charVerifications.Count,
                    ExpectedChar: expectedChar,
                    ObservedChar: OcvTemplateMatcher.UnmatchedRejectSymbol,
                    MatchScore: 0f,
                    ConfusionMargin: 0f,
                    MissingStrokeRatio: 1f,
                    BleedRatio: 0f,
                    LocalContrastGv: 0f,
                    StartCol: anchorCol,
                    EndCol: anchorCol,
                    TopRow: 0,
                    BottomRow: Math.Max(0, stripH - 1),
                    PrecededBySpace: false,
                    Verdict: OcvVerdictKind.Ng,
                    Cause: delCause));

                minMatchScore = 0f;
                maxMissingRatio = 1f;
                hasAnyNg = true;
                firstNgCause ??= $"문자 #{charVerifications.Count}({missingLabel}): {delCause}";
                continue;
            }

            OcvSequenceAligner.ObservedBoxSummary obs = obsBoxes[step.BoxIndex];
            SegmentedCharBox box = obs.Box;
            if (box.PrecededBySpace && decodedBuilder.Length > 0)
            {
                decodedBuilder.Append(' ');
            }

            if (step.Kind == OcvSequenceAligner.AlignmentStepKind.Insertion)
            {
                bool allowTrailingExtra = !effectiveSpec.StrictLengthMatch &&
                                          string.IsNullOrEmpty(effectiveSpec.CompactExpectedText) &&
                                          step.SlotIndex >= targetSlotCount;
                if (!allowTrailingExtra)
                {
                    char insertedChar = obs.UnconstrainedChar;
                    decodedBuilder.Append(insertedChar);
                    string insCause = $"삽입(과분할): 기대 슬롯에 없는 추가 문자 조각 관측 ('{insertedChar}')";
                    charVerifications.Add(new OcvCharacterVerification(
                        CharIndex: charVerifications.Count,
                        ExpectedChar: null,
                        ObservedChar: insertedChar,
                        MatchScore: obs.UnconstrainedScore,
                        ConfusionMargin: obs.UnconstrainedMargin,
                        MissingStrokeRatio: 0f,
                        BleedRatio: box.InterCharBridgeDensity,
                        LocalContrastGv: obs.ContrastGv,
                        StartCol: box.StartCol,
                        EndCol: box.EndCol,
                        TopRow: box.TopRow,
                        BottomRow: box.BottomRow,
                        PrecededBySpace: box.PrecededBySpace,
                        Verdict: OcvVerdictKind.Ng,
                        Cause: insCause));

                    sumMatchScore += obs.UnconstrainedScore;
                    scoredCount++;
                    minMatchScore = MathF.Min(minMatchScore, obs.UnconstrainedScore);
                    minContrastGv = MathF.Min(minContrastGv, obs.ContrastGv);
                    maxBleedRatio = MathF.Max(maxBleedRatio, box.InterCharBridgeDensity);
                    hasAnyNg = true;
                    firstNgCause ??= $"문자 #{charVerifications.Count}('{insertedChar}'): {insCause}";
                    continue;
                }
            }

            ReadOnlySpan<float> candGrid = allGrids.Slice(obs.FeatureOffset, IndustrialOcrGlyphTemplate.GridLength);
            ReadOnlySpan<float> candCl2n = allCl2ns.Slice(obs.FeatureOffset, IndustrialOcrGlyphTemplate.GridLength);
            ReadOnlySpan<float> candQuads = allQuads.Slice(obs.QuadOffset, IndustrialOcrGlyphTemplate.QuadrantCount);

            int evalSlotIndex = step.Kind == OcvSequenceAligner.AlignmentStepKind.Pair ? step.SlotIndex : targetSlotCount;
            OcvTemplateMatcher.TemplateMatchOutcome constrained = OcvTemplateMatcher.MatchSlotWithFullTemplateMargin(
                _goldenFont,
                candGrid,
                candCl2n,
                candQuads,
                evalSlotIndex,
                effectiveSpec);

            char? slotExpectedChar = step.Kind == OcvSequenceAligner.AlignmentStepKind.Pair
                ? effectiveSpec.GetExpectedCharacterAt(evalSlotIndex)
                : null;
            char candidateSlotChar = constrained.BestTemplate is not null
                ? constrained.BestChar
                : OcvTemplateMatcher.UnmatchedRejectSymbol;
            float matchScore = constrained.BestScore;

            bool hasDominantOutsideMaskGlyph =
                obs.UnconstrainedTemplate is not null &&
                obs.UnconstrainedChar != candidateSlotChar &&
                (!effectiveSpec.AllowsCharacterAt(evalSlotIndex, obs.UnconstrainedChar) &&
                 (obs.UnconstrainedScore - matchScore) >= effectiveOptions.MinConfusionMargin);

            // Report the actual dominant glyph when an out-of-mask character clearly wins (e.g., '12B4' instead of hiding 'B' as '8').
            char reportedObservedChar = hasDominantOutsideMaskGlyph
                ? obs.UnconstrainedChar
                : candidateSlotChar;

            IndustrialOcrGlyphTemplate? strokeRefTemplate = OcvTemplateMatcher.SelectStrokeReferenceTemplate(
                _goldenFont,
                slotExpectedChar,
                candGrid,
                candCl2n,
                candQuads,
                constrained.BestTemplate);

            float missingRatio = 0f;
            float bleedRatio = box.InterCharBridgeDensity;
            if (strokeRefTemplate is not null)
            {
                (float gridMissing, float gridBleed) = OcvTemplateMatcher.ComputeShiftTolerantStrokeMetrics(
                    candGrid,
                    strokeRefTemplate.NormalizedGrid);
                missingRatio = gridMissing;
                bleedRatio = MathF.Max(bleedRatio, gridBleed);
            }

            (OcvVerdictKind charVerdict, string charCause) = EvaluateCharacterVerdict(
                slotExpectedChar,
                candidateSlotChar,
                matchScore,
                constrained.FullTemplateConfusionMargin,
                obs.UnconstrainedChar,
                obs.UnconstrainedScore,
                obs.UnconstrainedTemplate is not null,
                obs.ContrastGv,
                missingRatio,
                bleedRatio,
                allowedByFormat: effectiveSpec.AllowsCharacterAt(evalSlotIndex, candidateSlotChar),
                effectiveOptions);

            decodedBuilder.Append(reportedObservedChar);

            int outIdx = charVerifications.Count;
            charVerifications.Add(new OcvCharacterVerification(
                CharIndex: outIdx,
                ExpectedChar: slotExpectedChar,
                ObservedChar: reportedObservedChar,
                MatchScore: matchScore,
                ConfusionMargin: constrained.FullTemplateConfusionMargin,
                MissingStrokeRatio: missingRatio,
                BleedRatio: bleedRatio,
                LocalContrastGv: obs.ContrastGv,
                StartCol: box.StartCol,
                EndCol: box.EndCol,
                TopRow: box.TopRow,
                BottomRow: box.BottomRow,
                PrecededBySpace: box.PrecededBySpace,
                Verdict: charVerdict,
                Cause: charCause));

            sumMatchScore += matchScore;
            scoredCount++;
            minMatchScore = MathF.Min(minMatchScore, matchScore);
            minContrastGv = MathF.Min(minContrastGv, obs.ContrastGv);
            maxMissingRatio = MathF.Max(maxMissingRatio, missingRatio);
            maxBleedRatio = MathF.Max(maxBleedRatio, bleedRatio);

            if (charVerdict == OcvVerdictKind.Ng)
            {
                hasAnyNg = true;
                firstNgCause ??= $"문자 #{outIdx + 1}('{reportedObservedChar}'): {charCause}";
            }
            else if (charVerdict == OcvVerdictKind.Review)
            {
                hasAnyReview = true;
                firstReviewCause ??= $"문자 #{outIdx + 1}('{reportedObservedChar}'): {charCause}";
            }
        }

        if (effectiveSpec.StrictLengthMatch)
        {
            if (!string.IsNullOrEmpty(effectiveSpec.CompactExpectedText) &&
                boxCount != effectiveSpec.CompactExpectedText.Length)
            {
                hasAnyNg = true;
                firstNgCause ??= $"문자 개수 불일치 (기대 {effectiveSpec.CompactExpectedText.Length}자 ≠ 검출 {boxCount}자)";
            }
            else if (effectiveSpec.SlotCount > 0 && boxCount != effectiveSpec.SlotCount)
            {
                hasAnyNg = true;
                firstNgCause ??= $"필드 마스크 길이 불일치 (마스크 {effectiveSpec.SlotCount}자 ≠ 검출 {boxCount}자)";
            }
        }

        OcvVerdictKind finalVerdict = hasAnyNg
            ? OcvVerdictKind.Ng
            : (hasAnyReview ? OcvVerdictKind.Review : OcvVerdictKind.Ok);

        string causeSummary = finalVerdict switch
        {
            OcvVerdictKind.Ng => firstNgCause ?? "인쇄 불량 (NG)",
            OcvVerdictKind.Review => firstReviewCause ?? "경계 품질 확인 필요 (Review)",
            _ => "모든 문자 일치 및 인쇄 품질 정상 (OK)",
        };

        string recommendedAction = finalVerdict switch
        {
            OcvVerdictKind.Ng => "제품 인쇄 상태(오인쇄·획 끊김·번짐·누락·과분할)를 확인하고 불량품을 분리하세요.",
            OcvVerdictKind.Review => "제품 NG와 구별되는 경계 경고입니다. 조명 대비·노즐 상태·ROI 정렬을 점검하거나 골든 샘플을 재확인하세요.",
            _ => "조치 불필요",
        };

        float meanScore = scoredCount > 0 ? (sumMatchScore / scoredCount) : 0f;
        return new OcvVerificationResult(
            Verdict: finalVerdict,
            DecodedText: decodedBuilder.ToString(),
            ExpectedText: effectiveSpec.ExpectedText,
            Characters: charVerifications,
            MeanMatchScore: meanScore,
            MinMatchScore: minMatchScore,
            MinContrastGv: minContrastGv == float.MaxValue ? 0f : minContrastGv,
            MaxMissingStrokeRatio: maxMissingRatio,
            MaxBleedRatio: maxBleedRatio,
            CauseSummary: causeSummary,
            RecommendedAction: recommendedAction);
    }

    private static (OcvVerdictKind Verdict, string Cause) EvaluateCharacterVerdict(
        char? expectedChar,
        char observedChar,
        float matchScore,
        float confusionMargin,
        char unconstrainedChar,
        float unconstrainedScore,
        bool hasUnconstrainedTemplate,
        float contrastGv,
        float missingRatio,
        float bleedRatio,
        bool allowedByFormat,
        OcvVerificationOptions options)
    {
        if (observedChar == OcvTemplateMatcher.UnmatchedRejectSymbol)
        {
            return (OcvVerdictKind.Ng, "치환/불일치: 허용된 필드 형식 내 일치 템플릿 없음");
        }

        if (!allowedByFormat)
        {
            return (OcvVerdictKind.Ng, $"치환(필드 형식 위반): 관측 '{observedChar}'");
        }

        if (hasUnconstrainedTemplate &&
            unconstrainedChar != observedChar &&
            (unconstrainedScore - matchScore) >= options.MinConfusionMargin)
        {
            return (OcvVerdictKind.Ng,
                $"치환(형식 밖 문자 더 일치): '{unconstrainedChar}'({unconstrainedScore:F2}) > 형식 후보 '{observedChar}'({matchScore:F2})");
        }

        if (expectedChar.HasValue && observedChar != expectedChar.Value)
        {
            return (OcvVerdictKind.Ng, $"치환(불일치): 기대 '{expectedChar.Value}' ≠ 관측 '{observedChar}'");
        }

        if (contrastGv < options.MinContrastGv)
        {
            return (OcvVerdictKind.Ng, $"심각한 저대비 ({contrastGv:F1} GV < {options.MinContrastGv:F1} GV)");
        }

        if (missingRatio > options.MaxMissingStrokeRatio)
        {
            return (OcvVerdictKind.Ng, $"획/도트 누락 과다 ({missingRatio:P0} > {options.MaxMissingStrokeRatio:P0})");
        }

        if (bleedRatio > options.MaxBleedRatio)
        {
            return (OcvVerdictKind.Ng, $"잉크 번짐/브릿지 과다 ({bleedRatio:P0} > {options.MaxBleedRatio:P0})");
        }

        if (matchScore < options.ReviewMatchScore)
        {
            return (OcvVerdictKind.Ng, $"치환/품질 미달: 템플릿 일치 점수 미달 ({matchScore:F2} < {options.ReviewMatchScore:F2})");
        }

        if (matchScore < options.PassMatchScore)
        {
            return (OcvVerdictKind.Review, $"일치 점수 경계 ({matchScore:F2} < {options.PassMatchScore:F2})");
        }

        if (contrastGv < options.ReviewContrastGv)
        {
            return (OcvVerdictKind.Review, $"인쇄 대비 낮음 ({contrastGv:F1} GV < {options.ReviewContrastGv:F1} GV)");
        }

        if (missingRatio > options.ReviewMissingStrokeRatio)
        {
            return (OcvVerdictKind.Review, $"미세 획 약화 감지 ({missingRatio:P0})");
        }

        if (bleedRatio > options.ReviewBleedRatio)
        {
            return (OcvVerdictKind.Review, $"미세 번짐 감지 ({bleedRatio:P0})");
        }

        if (confusionMargin < options.MinConfusionMargin)
        {
            return (OcvVerdictKind.Review, $"유사 문자 마진 부족 (Δ={confusionMargin:F3})");
        }

        return (OcvVerdictKind.Ok, "정상");
    }

    private static OcvVerificationResult CreateFailureResult(
        string? expectedText,
        float stripContrast,
        float maxMissingRatio,
        string causeSummary,
        string recommendedAction)
    {
        return new OcvVerificationResult(
            Verdict: OcvVerdictKind.Ng,
            DecodedText: string.Empty,
            ExpectedText: expectedText,
            Characters: [],
            MeanMatchScore: 0f,
            MinMatchScore: 0f,
            MinContrastGv: stripContrast,
            MaxMissingStrokeRatio: maxMissingRatio,
            MaxBleedRatio: 0f,
            CauseSummary: causeSummary,
            RecommendedAction: recommendedAction);
    }

    private static OcrMaxSegmentParams BuildSegmentParams(int stripW, int stripH, OcvVerificationOptions options)
    {
        return new OcrMaxSegmentParams(
            printMethod: options.PrintMethod,
            rectifiedWidth: stripW,
            rectifiedHeight: stripH,
            minCharWidth: options.MinCharWidth,
            maxCharWidth: options.MaxCharWidth,
            expectedPitch: options.ExpectedPitch,
            dotClosingRadiusX: options.DotClosingRadiusX,
            dotClosingRadiusY: options.DotClosingRadiusY,
            foregroundContrastOffset: options.ForegroundContrastOffset,
            darkOnLight: options.DarkOnLight);
    }

    private static (int X0, int Y0, int Width, int Height, float LocalContrastGv) ResolveGlyphFeatureWindow(
        ReadOnlySpan<byte> strip,
        int stripW,
        int stripH,
        in SegmentedCharBox box)
    {
        float innerContrast = ComputeLocalBoxContrastWithMargin(strip, stripW, stripH, in box, margin: 0);
        float localContrast = ComputeLocalBoxContrastWithMargin(strip, stripW, stripH, in box, margin: LocalContrastContextRingMargin);

        bool lacksInternalBackground = localContrast > (innerContrast * SolidGlyphMarginContrastRatio);
        int pad = lacksInternalBackground ? 1 : 0;

        int x0 = Math.Max(0, box.StartCol - pad);
        int x1 = Math.Min(stripW - 1, box.EndCol + pad);
        int y0 = Math.Max(0, box.TopRow - pad);
        int y1 = Math.Min(stripH - 1, box.BottomRow + pad);
        return (x0, y0, (x1 - x0) + 1, (y1 - y0) + 1, localContrast);
    }

    private static float ComputeLocalBoxContrastWithMargin(
        ReadOnlySpan<byte> strip,
        int stripW,
        int stripH,
        in SegmentedCharBox box,
        int margin)
    {
        int x0 = Math.Max(0, box.StartCol - margin);
        int x1 = Math.Min(stripW - 1, box.EndCol + margin);
        int y0 = Math.Max(0, box.TopRow - margin);
        int y1 = Math.Min(stripH - 1, box.BottomRow + margin);

        byte min = byte.MaxValue;
        byte max = byte.MinValue;
        for (int r = y0; r <= y1; r++)
        {
            int rowOff = r * stripW;
            for (int c = x0; c <= x1; c++)
            {
                byte p = strip[rowOff + c];
                if (p < min)
                {
                    min = p;
                }

                if (p > max)
                {
                    max = p;
                }
            }
        }

        return max >= min ? (max - min) : 0f;
    }

    private static float ComputePeakToPeakContrast(ReadOnlySpan<byte> pixels)
    {
        if (pixels.IsEmpty)
        {
            return 0f;
        }

        byte min = byte.MaxValue;
        byte max = byte.MinValue;
        for (int i = 0; i < pixels.Length; i++)
        {
            byte p = pixels[i];
            if (p < min)
            {
                min = p;
            }

            if (p > max)
            {
                max = p;
            }
        }

        return max - min;
    }
}
