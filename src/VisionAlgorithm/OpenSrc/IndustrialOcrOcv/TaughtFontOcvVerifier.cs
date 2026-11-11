using System;
using System.Collections.Generic;
using System.Text;
using VisionAlgorithm.OpenSrc.Common;

namespace VisionAlgorithm.OpenSrc.IndustrialOcrOcv;

/// <summary>
/// Dedicated taught-font Optical Character Verification (OCV) verifier separated from general-purpose OCR.
/// Evaluates segmented glyphs against a user-taught <see cref="IndustrialOcrFontModel"/> under an optional
/// <see cref="OcrFieldFormatSpec"/> candidate constraint using sequence alignment so a single split or missing
/// character does not cascade false mismatches across subsequent intact characters, without ever rewriting
/// mismatched characters into expected text.
/// </summary>
public sealed class TaughtFontOcvVerifier
{
    private const float GoldenStrokeActiveFloor = 0.45f;
    private const float CandidateStrokePresentFloor = 0.22f;
    private const float GoldenBackgroundSafeCeiling = 0.12f;
    private const float CandidateBleedInkFloor = 0.48f;
    private const float Cl2nScoreWeight = 0.45f;
    private const float DirectGridScoreWeight = 0.30f;
    private const float QuadrantScoreWeight = 0.25f;
    private const float QuadrantMaxDiffWeight = 0.55f;
    private const float QuadrantMeanDiffWeight = 0.45f;
    private const float SolidGlyphMarginContrastRatio = 2.5f;
    private const float CosineNormalizationScale = 0.5f;
    private const float CosineNormalizationBias = 0.5f;
    private const float AlignmentDeletionCost = 1.00f;
    private const float AlignmentStrictInsertionCost = 1.00f;
    private const float AlignmentTrailingRelaxedInsertionCost = 0.20f;
    private const float AlignmentMatchDissimilarityWeight = 0.35f;
    private const float AlignmentSubstitutionBaseCost = 1.15f;
    private const char UnmatchedRejectSymbol = '?';

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

        int stripW = Math.Max(16, image.Width);
        int stripH = Math.Max(12, image.Height);
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
            return new OcvVerificationResult(
                Verdict: OcvVerdictKind.Ng,
                DecodedText: string.Empty,
                ExpectedText: effectiveSpec.ExpectedText,
                Characters: [],
                MeanMatchScore: 0f,
                MinMatchScore: 0f,
                MinContrastGv: stripContrast,
                MaxMissingStrokeRatio: 1f,
                MaxBleedRatio: 0f,
                CauseSummary: "문자 분할 실패 (검출된 글자 0개)",
                RecommendedAction: "인쇄 누락 여부를 확인하고 ROI 위치·명암 극성(DarkOnLight)·전경 대비 오프셋을 점검하세요.");
        }

        if (boxCount > effectiveOptions.MaxLineCharacters)
        {
            float stripContrast = ComputePeakToPeakContrast(rectifiedStrip);
            return new OcvVerificationResult(
                Verdict: OcvVerdictKind.Ng,
                DecodedText: string.Empty,
                ExpectedText: effectiveSpec.ExpectedText,
                Characters: [],
                MeanMatchScore: 0f,
                MinMatchScore: 0f,
                MinContrastGv: stripContrast,
                MaxMissingStrokeRatio: 0f,
                MaxBleedRatio: 0f,
                CauseSummary: $"최대 허용 문자 수 초과 (검출 {boxCount}자 > 상한 {effectiveOptions.MaxLineCharacters}자 — 과분할 또는 노이즈 혼입)",
                RecommendedAction: "배경 노이즈·조명 반사·전경 대비 오프셋을 점검하거나 필요한 경우 MaxLineCharacters 상한을 조정하세요.");
        }

        // Precompute normalized features and unconstrained match for every observed box
        var obsFeatures = new ObservedBoxFeatures[boxCount];
        Span<float> hProjScratch = stackalloc float[IndustrialOcrGlyphTemplate.GridHeight];
        Span<float> vProjScratch = stackalloc float[IndustrialOcrGlyphTemplate.GridWidth];

        for (int j = 0; j < boxCount; j++)
        {
            SegmentedCharBox box = boxes[j];
            var grid = new float[IndustrialOcrGlyphTemplate.GridLength];
            var cl2n = new float[IndustrialOcrGlyphTemplate.GridLength];
            var quads = new float[IndustrialOcrGlyphTemplate.QuadrantCount];

            (int cropX0, int cropY0, int cropW, int cropH, float localContrastGv) =
                ResolveGlyphFeatureWindow(rectifiedStrip, stripW, stripH, in box);

            ReadOnlySpan<byte> patchSlice = rectifiedStrip.Slice((cropY0 * stripW) + cropX0);
            ReadOnlyImageView2D<byte> charView = new(patchSlice, cropW, cropH, stripW);

            IndustrialOcrTrainer.ExtractNormalizedGlyphFeatures(
                charView,
                effectiveOptions.DarkOnLight,
                grid,
                hProjScratch,
                vProjScratch,
                quads,
                out float innerContrastGv);
            float contrastGv = MathF.Max(innerContrastGv, localContrastGv);

            SimpleShotNormalizer.CenterAndL2Normalize(grid, _goldenFont.GlobalMean, cl2n);

            MatchUnconstrainedAgainstTaughtFont(
                grid,
                cl2n,
                quads,
                out char uncChar,
                out float uncScore,
                out float uncMargin,
                out IndustrialOcrGlyphTemplate? uncTpl);

            obsFeatures[j] = new ObservedBoxFeatures(
                Box: box,
                Grid: grid,
                Cl2n: cl2n,
                Quads: quads,
                ContrastGv: contrastGv,
                UnconstrainedChar: uncTpl is not null ? uncChar : UnmatchedRejectSymbol,
                UnconstrainedScore: uncScore,
                UnconstrainedMargin: uncMargin,
                UnconstrainedTemplate: uncTpl);
        }

        int targetSlotCount = !string.IsNullOrEmpty(effectiveSpec.CompactExpectedText)
            ? effectiveSpec.CompactExpectedText.Length
            : effectiveSpec.SlotCount;

        List<AlignmentStep> steps = targetSlotCount > 0
            ? AlignSlotsToObservedBoxes(targetSlotCount, obsFeatures, effectiveSpec, effectiveOptions)
            : BuildUnalignedObservedSteps(boxCount);

        var charVerifications = new List<OcvCharacterVerification>(steps.Count);
        var decodedBuilder = new StringBuilder(boxCount + 8);

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
            AlignmentStep step = steps[s];

            if (step.Kind == AlignmentStepKind.Deletion)
            {
                int slotIdx = step.SlotIndex;
                char? expectedChar = effectiveSpec.GetExpectedCharacterAt(slotIdx);
                string missingLabel = expectedChar.HasValue ? $"'{expectedChar.Value}'" : $"슬롯 #{slotIdx + 1}";
                string delCause = $"삭제(누락): 기대 문자 {missingLabel} 미검출";

                int anchorCol = EstimateDeletedSlotColumn(steps, s, obsFeatures, stripW);
                charVerifications.Add(new OcvCharacterVerification(
                    CharIndex: charVerifications.Count,
                    ExpectedChar: expectedChar,
                    ObservedChar: UnmatchedRejectSymbol,
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

            ObservedBoxFeatures obs = obsFeatures[step.BoxIndex];
            SegmentedCharBox box = obs.Box;
            if (box.PrecededBySpace && decodedBuilder.Length > 0)
            {
                decodedBuilder.Append(' ');
            }

            if (step.Kind == AlignmentStepKind.Insertion)
            {
                char insertedChar = obs.UnconstrainedChar;
                decodedBuilder.Append(insertedChar);

                bool allowTrailingExtra = !effectiveSpec.StrictLengthMatch &&
                                          string.IsNullOrEmpty(effectiveSpec.CompactExpectedText) &&
                                          step.SlotIndex >= targetSlotCount;

                if (!allowTrailingExtra)
                {
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
                    if (obs.UnconstrainedScore < minMatchScore) minMatchScore = obs.UnconstrainedScore;
                    if (obs.ContrastGv < minContrastGv) minContrastGv = obs.ContrastGv;
                    if (box.InterCharBridgeDensity > maxBleedRatio) maxBleedRatio = box.InterCharBridgeDensity;
                    hasAnyNg = true;
                    firstNgCause ??= $"문자 #{charVerifications.Count}('{insertedChar}'): {insCause}";
                    continue;
                }
            }

            int evalSlotIndex = step.Kind == AlignmentStepKind.Pair ? step.SlotIndex : targetSlotCount;
            MatchAgainstTaughtFont(
                obs.Grid,
                obs.Cl2n,
                obs.Quads,
                charIndex: evalSlotIndex,
                effectiveSpec,
                out char constrainedBestChar,
                out float constrainedBestScore,
                out float constrainedConfusionMargin,
                out IndustrialOcrGlyphTemplate? constrainedBestTemplate);

            char? slotExpectedChar = step.Kind == AlignmentStepKind.Pair
                ? effectiveSpec.GetExpectedCharacterAt(evalSlotIndex)
                : null;
            char observedChar = constrainedBestTemplate is not null
                ? constrainedBestChar
                : UnmatchedRejectSymbol;
            float matchScore = constrainedBestScore;

            IndustrialOcrGlyphTemplate? strokeRefTemplate = SelectStrokeReferenceTemplate(
                slotExpectedChar,
                obs.Grid,
                obs.Cl2n,
                obs.Quads,
                constrainedBestTemplate);

            float missingRatio = 0f;
            float bleedRatio = box.InterCharBridgeDensity;
            if (strokeRefTemplate is not null)
            {
                ComputeShiftTolerantStrokeMetrics(
                    obs.Grid,
                    strokeRefTemplate.NormalizedGrid,
                    out float gridMissing,
                    out float gridBleed);
                missingRatio = gridMissing;
                bleedRatio = MathF.Max(bleedRatio, gridBleed);
            }

            (OcvVerdictKind charVerdict, string charCause) = EvaluateCharacterVerdict(
                slotExpectedChar,
                observedChar,
                matchScore,
                constrainedConfusionMargin,
                obs.ContrastGv,
                missingRatio,
                bleedRatio,
                allowedByFormat: effectiveSpec.AllowsCharacterAt(evalSlotIndex, observedChar),
                effectiveOptions);

            if (step.Kind != AlignmentStepKind.Insertion)
            {
                decodedBuilder.Append(observedChar);
            }

            int outIdx = charVerifications.Count;
            charVerifications.Add(new OcvCharacterVerification(
                CharIndex: outIdx,
                ExpectedChar: slotExpectedChar,
                ObservedChar: observedChar,
                MatchScore: matchScore,
                ConfusionMargin: constrainedConfusionMargin,
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
            if (matchScore < minMatchScore) minMatchScore = matchScore;
            if (obs.ContrastGv < minContrastGv) minContrastGv = obs.ContrastGv;
            if (missingRatio > maxMissingRatio) maxMissingRatio = missingRatio;
            if (bleedRatio > maxBleedRatio) maxBleedRatio = bleedRatio;

            if (charVerdict == OcvVerdictKind.Ng)
            {
                hasAnyNg = true;
                firstNgCause ??= $"문자 #{outIdx + 1}('{observedChar}'): {charCause}";
            }
            else if (charVerdict == OcvVerdictKind.Review)
            {
                hasAnyReview = true;
                firstReviewCause ??= $"문자 #{outIdx + 1}('{observedChar}'): {charCause}";
            }
        }

        // Strict length enforcement as safety net
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

        int stripW = Math.Max(16, image.Width);
        int stripH = Math.Max(12, image.Height);
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

    private List<AlignmentStep> AlignSlotsToObservedBoxes(
        int slotCount,
        ObservedBoxFeatures[] obsFeatures,
        OcrFieldFormatSpec spec,
        OcvVerificationOptions options)
    {
        int m = slotCount;
        int n = obsFeatures.Length;
        float[,] dp = new float[m + 1, n + 1];
        byte[,] back = new byte[m + 1, n + 1]; // 1 = Pair, 2 = Deletion (slot only), 3 = Insertion (box only)

        for (int i = 1; i <= m; i++)
        {
            dp[i, 0] = i * AlignmentDeletionCost;
            back[i, 0] = 2;
        }

        for (int j = 1; j <= n; j++)
        {
            dp[0, j] = j * AlignmentStrictInsertionCost;
            back[0, j] = 3;
        }

        bool hasExpectedText = !string.IsNullOrEmpty(spec.CompactExpectedText);

        for (int i = 1; i <= m; i++)
        {
            int slotIdx = i - 1;
            char? expectedChar = spec.GetExpectedCharacterAt(slotIdx);

            for (int j = 1; j <= n; j++)
            {
                ObservedBoxFeatures obs = obsFeatures[j - 1];
                float pairCost = ComputeSlotBoxPairCost(slotIdx, expectedChar, hasExpectedText, obs, spec, options);

                float costPair = dp[i - 1, j - 1] + pairCost;
                float costDel = dp[i - 1, j] + AlignmentDeletionCost;

                float insPenalty = (!spec.StrictLengthMatch && !hasExpectedText && i == m)
                    ? AlignmentTrailingRelaxedInsertionCost
                    : AlignmentStrictInsertionCost;
                float costIns = dp[i, j - 1] + insPenalty;

                if (costPair <= costDel && costPair <= costIns)
                {
                    dp[i, j] = costPair;
                    back[i, j] = 1;
                }
                else if (costDel <= costIns)
                {
                    dp[i, j] = costDel;
                    back[i, j] = 2;
                }
                else
                {
                    dp[i, j] = costIns;
                    back[i, j] = 3;
                }
            }
        }

        var reversed = new List<AlignmentStep>(m + n);
        int ci = m;
        int cj = n;
        while (ci > 0 || cj > 0)
        {
            byte op = back[ci, cj];
            if (ci > 0 && cj > 0 && op == 1)
            {
                reversed.Add(new AlignmentStep(AlignmentStepKind.Pair, ci - 1, cj - 1));
                ci--;
                cj--;
            }
            else if (ci > 0 && (cj == 0 || op == 2))
            {
                reversed.Add(new AlignmentStep(AlignmentStepKind.Deletion, ci - 1, -1));
                ci--;
            }
            else
            {
                reversed.Add(new AlignmentStep(AlignmentStepKind.Insertion, ci, cj - 1));
                cj--;
            }
        }

        reversed.Reverse();
        return reversed;
    }

    private float ComputeSlotBoxPairCost(
        int slotIdx,
        char? expectedChar,
        bool hasExpectedText,
        in ObservedBoxFeatures obs,
        OcrFieldFormatSpec spec,
        OcvVerificationOptions options)
    {
        MatchAgainstTaughtFont(
            obs.Grid,
            obs.Cl2n,
            obs.Quads,
            slotIdx,
            spec,
            out char slotChar,
            out float slotScore,
            out _,
            out IndustrialOcrGlyphTemplate? slotTpl);

        if (hasExpectedText && expectedChar.HasValue)
        {
            if (slotTpl is not null && slotChar == expectedChar.Value)
            {
                return AlignmentMatchDissimilarityWeight * (1.0f - slotScore);
            }

            float expectedSim = ComputeBestTemplateScoreForChar(
                expectedChar.Value,
                obs.Grid,
                obs.Cl2n,
                obs.Quads);
            return AlignmentSubstitutionBaseCost + (AlignmentMatchDissimilarityWeight * (1.0f - expectedSim));
        }

        bool allowed = slotTpl is not null && spec.AllowsCharacterAt(slotIdx, slotChar);
        if (allowed && slotScore >= options.ReviewMatchScore)
        {
            return AlignmentMatchDissimilarityWeight * (1.0f - slotScore);
        }

        return AlignmentSubstitutionBaseCost + (AlignmentMatchDissimilarityWeight * (1.0f - slotScore));
    }

    private float ComputeBestTemplateScoreForChar(
        char targetChar,
        ReadOnlySpan<float> candGrid,
        ReadOnlySpan<float> candCl2n,
        ReadOnlySpan<float> candQuads)
    {
        float bestScore = 0f;
        ReadOnlySpan<IndustrialOcrGlyphTemplate> templates = _goldenFont.Templates;
        for (int t = 0; t < templates.Length; t++)
        {
            IndustrialOcrGlyphTemplate tpl = templates[t];
            if (tpl.Character != targetChar)
            {
                continue;
            }

            float score = ComputeCombinedTemplateScore(candGrid, candCl2n, candQuads, tpl);
            if (score > bestScore)
            {
                bestScore = score;
            }
        }

        return bestScore;
    }

    private static List<AlignmentStep> BuildUnalignedObservedSteps(int boxCount)
    {
        var steps = new List<AlignmentStep>(boxCount);
        for (int j = 0; j < boxCount; j++)
        {
            steps.Add(new AlignmentStep(AlignmentStepKind.Pair, j, j));
        }

        return steps;
    }

    private static int EstimateDeletedSlotColumn(
        List<AlignmentStep> steps,
        int stepIndex,
        ObservedBoxFeatures[] obsFeatures,
        int stripW)
    {
        for (int p = stepIndex - 1; p >= 0; p--)
        {
            if (steps[p].BoxIndex >= 0)
            {
                return Math.Min(stripW - 1, obsFeatures[steps[p].BoxIndex].Box.EndCol + 1);
            }
        }

        for (int q = stepIndex + 1; q < steps.Count; q++)
        {
            if (steps[q].BoxIndex >= 0)
            {
                return Math.Max(0, obsFeatures[steps[q].BoxIndex].Box.StartCol);
            }
        }

        return stripW / 2;
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

    private void MatchUnconstrainedAgainstTaughtFont(
        ReadOnlySpan<float> candGrid,
        ReadOnlySpan<float> candCl2n,
        ReadOnlySpan<float> candQuads,
        out char bestChar,
        out float bestScore,
        out float confusionMargin,
        out IndustrialOcrGlyphTemplate? bestTemplate)
    {
        bestChar = UnmatchedRejectSymbol;
        bestScore = 0f;
        float runnerUpScore = 0f;
        bestTemplate = null;

        ReadOnlySpan<IndustrialOcrGlyphTemplate> templates = _goldenFont.Templates;
        for (int t = 0; t < templates.Length; t++)
        {
            IndustrialOcrGlyphTemplate tpl = templates[t];
            float score = ComputeCombinedTemplateScore(candGrid, candCl2n, candQuads, tpl);

            if (score > bestScore)
            {
                if (bestTemplate is not null && bestChar != tpl.Character)
                {
                    runnerUpScore = MathF.Max(runnerUpScore, bestScore);
                }

                bestScore = score;
                bestChar = tpl.Character;
                bestTemplate = tpl;
            }
            else if (tpl.Character != bestChar && score > runnerUpScore)
            {
                runnerUpScore = score;
            }
        }

        confusionMargin = bestTemplate is not null
            ? MathF.Max(0f, bestScore - runnerUpScore)
            : 0f;
    }

    private void MatchAgainstTaughtFont(
        ReadOnlySpan<float> candGrid,
        ReadOnlySpan<float> candCl2n,
        ReadOnlySpan<float> candQuads,
        int charIndex,
        OcrFieldFormatSpec fieldSpec,
        out char constrainedBestChar,
        out float constrainedBestScore,
        out float constrainedConfusionMargin,
        out IndustrialOcrGlyphTemplate? constrainedBestTemplate)
    {
        constrainedBestChar = UnmatchedRejectSymbol;
        constrainedBestScore = 0f;
        float constrainedRunnerUpScore = 0f;
        constrainedBestTemplate = null;

        ReadOnlySpan<IndustrialOcrGlyphTemplate> templates = _goldenFont.Templates;
        for (int t = 0; t < templates.Length; t++)
        {
            IndustrialOcrGlyphTemplate tpl = templates[t];
            if (!fieldSpec.AllowsCharacterAt(charIndex, tpl.Character))
            {
                continue;
            }

            float score = ComputeCombinedTemplateScore(candGrid, candCl2n, candQuads, tpl);

            if (score > constrainedBestScore)
            {
                if (constrainedBestTemplate is not null && constrainedBestChar != tpl.Character)
                {
                    constrainedRunnerUpScore = MathF.Max(constrainedRunnerUpScore, constrainedBestScore);
                }

                constrainedBestScore = score;
                constrainedBestChar = tpl.Character;
                constrainedBestTemplate = tpl;
            }
            else if (tpl.Character != constrainedBestChar && score > constrainedRunnerUpScore)
            {
                constrainedRunnerUpScore = score;
            }
        }

        constrainedConfusionMargin = constrainedBestTemplate is not null
            ? MathF.Max(0f, constrainedBestScore - constrainedRunnerUpScore)
            : 0f;
    }

    private IndustrialOcrGlyphTemplate? SelectStrokeReferenceTemplate(
        char? expectedChar,
        ReadOnlySpan<float> candGrid,
        ReadOnlySpan<float> candCl2n,
        ReadOnlySpan<float> candQuads,
        IndustrialOcrGlyphTemplate? fallbackMatchedTemplate)
    {
        if (expectedChar is null)
        {
            return fallbackMatchedTemplate;
        }

        IndustrialOcrGlyphTemplate? bestExpectedTemplate = null;
        float bestExpectedScore = -1f;
        ReadOnlySpan<IndustrialOcrGlyphTemplate> templates = _goldenFont.Templates;
        for (int t = 0; t < templates.Length; t++)
        {
            IndustrialOcrGlyphTemplate tpl = templates[t];
            if (tpl.Character != expectedChar.Value)
            {
                continue;
            }

            float score = ComputeCombinedTemplateScore(candGrid, candCl2n, candQuads, tpl);
            if (score > bestExpectedScore)
            {
                bestExpectedScore = score;
                bestExpectedTemplate = tpl;
            }
        }

        return bestExpectedTemplate ?? fallbackMatchedTemplate;
    }

    private static float ComputeCombinedTemplateScore(
        ReadOnlySpan<float> candGrid,
        ReadOnlySpan<float> candCl2n,
        ReadOnlySpan<float> candQuads,
        IndustrialOcrGlyphTemplate tpl)
    {
        float dot = OcrMaxFieldingMatcher.ComputeDotProductAvx2(candCl2n, tpl.Cl2nFeature);
        float cl2nSim = Math.Clamp((dot * CosineNormalizationScale) + CosineNormalizationBias, 0f, 1f);

        ReadOnlySpan<float> tplGrid = tpl.NormalizedGrid;
        float gridAbsDiffSum = 0f;
        for (int k = 0; k < IndustrialOcrGlyphTemplate.GridLength; k++)
        {
            gridAbsDiffSum += MathF.Abs(candGrid[k] - tplGrid[k]);
        }

        float gridDirectSim = Math.Clamp(
            1.0f - (gridAbsDiffSum / IndustrialOcrGlyphTemplate.GridLength),
            0f,
            1f);

        ReadOnlySpan<float> tplQuads = tpl.QuadrantDensities;
        float maxQuadDiff = 0f;
        float sumQuadDiff = 0f;
        int quadLen = Math.Min(candQuads.Length, tplQuads.Length);
        for (int q = 0; q < quadLen; q++)
        {
            float d = MathF.Abs(candQuads[q] - tplQuads[q]);
            sumQuadDiff += d;
            if (d > maxQuadDiff)
            {
                maxQuadDiff = d;
            }
        }

        float quadPenalty = Math.Clamp(
            (QuadrantMaxDiffWeight * maxQuadDiff) + (QuadrantMeanDiffWeight * (sumQuadDiff / Math.Max(1, quadLen))),
            0f,
            1f);

        return Math.Clamp(
            (Cl2nScoreWeight * cl2nSim) +
            (DirectGridScoreWeight * gridDirectSim) +
            (QuadrantScoreWeight * (1.0f - quadPenalty)),
            0f,
            1f);
    }

    private static void ComputeShiftTolerantStrokeMetrics(
        ReadOnlySpan<float> candidateGrid,
        ReadOnlySpan<float> goldenGrid,
        out float missingStrokeRatio,
        out float bleedRatio)
    {
        const int gw = IndustrialOcrGlyphTemplate.GridWidth;
        const int gh = IndustrialOcrGlyphTemplate.GridHeight;

        int goldenActiveCount = 0;
        int missingActiveCount = 0;
        int goldenSafeBgCount = 0;
        int bleedBgCount = 0;

        for (int gy = 0; gy < gh; gy++)
        {
            int yMin = Math.Max(0, gy - 1);
            int yMax = Math.Min(gh - 1, gy + 1);

            for (int gx = 0; gx < gw; gx++)
            {
                int xMin = Math.Max(0, gx - 1);
                int xMax = Math.Min(gw - 1, gx + 1);
                int centerIdx = (gy * gw) + gx;

                if (goldenGrid[centerIdx] >= GoldenStrokeActiveFloor)
                {
                    goldenActiveCount++;
                    float maxCandNeighborhood = 0f;
                    for (int ny = yMin; ny <= yMax; ny++)
                    {
                        for (int nx = xMin; nx <= xMax; nx++)
                        {
                            float v = candidateGrid[(ny * gw) + nx];
                            if (v > maxCandNeighborhood) maxCandNeighborhood = v;
                        }
                    }

                    if (maxCandNeighborhood < CandidateStrokePresentFloor)
                    {
                        missingActiveCount++;
                    }
                }

                float maxGoldenNeighborhood = 0f;
                for (int ny = yMin; ny <= yMax; ny++)
                {
                    for (int nx = xMin; nx <= xMax; nx++)
                    {
                        float v = goldenGrid[(ny * gw) + nx];
                        if (v > maxGoldenNeighborhood) maxGoldenNeighborhood = v;
                    }
                }

                if (maxGoldenNeighborhood < GoldenBackgroundSafeCeiling)
                {
                    goldenSafeBgCount++;
                    if (candidateGrid[centerIdx] >= CandidateBleedInkFloor)
                    {
                        bleedBgCount++;
                    }
                }
            }
        }

        missingStrokeRatio = goldenActiveCount > 0
            ? (float)missingActiveCount / goldenActiveCount
            : 0f;
        bleedRatio = goldenSafeBgCount > 0
            ? (float)bleedBgCount / goldenSafeBgCount
            : 0f;
    }

    private static (OcvVerdictKind Verdict, string Cause) EvaluateCharacterVerdict(
        char? expectedChar,
        char observedChar,
        float matchScore,
        float confusionMargin,
        float contrastGv,
        float missingRatio,
        float bleedRatio,
        bool allowedByFormat,
        OcvVerificationOptions options)
    {
        if (observedChar == UnmatchedRejectSymbol)
        {
            return (OcvVerdictKind.Ng, "치환/불일치: 허용된 필드 형식 내 일치 템플릿 없음");
        }

        if (!allowedByFormat)
        {
            return (OcvVerdictKind.Ng, $"치환(필드 형식 위반): 관측 '{observedChar}'");
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

    private static (int X0, int Y0, int Width, int Height, float LocalContrastGv) ResolveGlyphFeatureWindow(
        ReadOnlySpan<byte> strip,
        int stripW,
        int stripH,
        in SegmentedCharBox box)
    {
        float innerContrast = ComputeLocalBoxContrastWithMargin(strip, stripW, stripH, in box, margin: 0);
        float localContrast = ComputeLocalBoxContrastWithMargin(strip, stripW, stripH, in box, margin: 2);

        // Include a 1-pixel context ring ONLY when the tight glyph bounding box is completely filled with
        // uniform foreground ink (e.g., solid vertical bar 'I', '-', '.') and therefore lacks background reference.
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
                if (p < min) min = p;
                if (p > max) max = p;
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
            if (p < min) min = p;
            if (p > max) max = p;
        }

        return max - min;
    }

    private enum AlignmentStepKind : byte
    {
        Pair = 0,
        Deletion = 1,
        Insertion = 2,
    }

    private readonly record struct AlignmentStep(
        AlignmentStepKind Kind,
        int SlotIndex,
        int BoxIndex);

    private readonly record struct ObservedBoxFeatures(
        SegmentedCharBox Box,
        float[] Grid,
        float[] Cl2n,
        float[] Quads,
        float ContrastGv,
        char UnconstrainedChar,
        float UnconstrainedScore,
        float UnconstrainedMargin,
        IndustrialOcrGlyphTemplate? UnconstrainedTemplate);
}
