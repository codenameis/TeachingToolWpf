using System;
using System.Collections.Generic;

namespace VisionAlgorithm.OpenSrc.IndustrialOcrOcv;

/// <summary>
/// Dynamic-programming sequence aligner pairing expected format slots to segmented glyph boxes so a single
/// missing or split character does not cascade false substitution errors across subsequent intact characters.
/// </summary>
internal static class OcvSequenceAligner
{
    private const byte OpPair = 1;
    private const byte OpDeletion = 2;
    private const byte OpInsertion = 3;

    private const float AlignmentDeletionCost = 1.00f;
    private const float AlignmentStrictInsertionCost = 1.00f;
    private const float AlignmentTrailingRelaxedInsertionCost = 0.20f;
    private const float AlignmentMatchDissimilarityWeight = 0.35f;
    private const float AlignmentSubstitutionBaseCost = 1.15f;

    internal enum AlignmentStepKind : byte
    {
        Pair = 0,
        Deletion = 1,
        Insertion = 2,
    }

    internal readonly record struct AlignmentStep(
        AlignmentStepKind Kind,
        int SlotIndex,
        int BoxIndex);

    internal readonly record struct ObservedBoxSummary(
        SegmentedCharBox Box,
        int FeatureOffset,
        int QuadOffset,
        float ContrastGv,
        char UnconstrainedChar,
        float UnconstrainedScore,
        float UnconstrainedMargin,
        IndustrialOcrGlyphTemplate? UnconstrainedTemplate);

    internal static List<AlignmentStep> AlignSlotsToObservedBoxes(
        IndustrialOcrFontModel goldenFont,
        int slotCount,
        ReadOnlySpan<ObservedBoxSummary> obsBoxes,
        ReadOnlySpan<float> allGrids,
        ReadOnlySpan<float> allCl2ns,
        ReadOnlySpan<float> allQuads,
        OcrFieldFormatSpec spec,
        OcvVerificationOptions options)
    {
        int m = slotCount;
        int n = obsBoxes.Length;
        float[,] dp = new float[m + 1, n + 1];
        byte[,] back = new byte[m + 1, n + 1];

        for (int i = 1; i <= m; i++)
        {
            dp[i, 0] = i * AlignmentDeletionCost;
            back[i, 0] = OpDeletion;
        }

        for (int j = 1; j <= n; j++)
        {
            dp[0, j] = j * AlignmentStrictInsertionCost;
            back[0, j] = OpInsertion;
        }

        bool hasExpectedText = !string.IsNullOrEmpty(spec.CompactExpectedText);

        for (int i = 1; i <= m; i++)
        {
            int slotIdx = i - 1;
            char? expectedChar = spec.GetExpectedCharacterAt(slotIdx);

            for (int j = 1; j <= n; j++)
            {
                ObservedBoxSummary obs = obsBoxes[j - 1];
                ReadOnlySpan<float> candGrid = allGrids.Slice(obs.FeatureOffset, IndustrialOcrGlyphTemplate.GridLength);
                ReadOnlySpan<float> candCl2n = allCl2ns.Slice(obs.FeatureOffset, IndustrialOcrGlyphTemplate.GridLength);
                ReadOnlySpan<float> candQuads = allQuads.Slice(obs.QuadOffset, IndustrialOcrGlyphTemplate.QuadrantCount);

                float pairCost = ComputeSlotBoxPairCost(
                    goldenFont,
                    slotIdx,
                    expectedChar,
                    hasExpectedText,
                    candGrid,
                    candCl2n,
                    candQuads,
                    spec,
                    options);

                float costPair = dp[i - 1, j - 1] + pairCost;
                float costDel = dp[i - 1, j] + AlignmentDeletionCost;

                float insPenalty = (!spec.StrictLengthMatch && !hasExpectedText && i == m)
                    ? AlignmentTrailingRelaxedInsertionCost
                    : AlignmentStrictInsertionCost;
                float costIns = dp[i, j - 1] + insPenalty;

                if (costPair <= costDel && costPair <= costIns)
                {
                    dp[i, j] = costPair;
                    back[i, j] = OpPair;
                }
                else if (costDel <= costIns)
                {
                    dp[i, j] = costDel;
                    back[i, j] = OpDeletion;
                }
                else
                {
                    dp[i, j] = costIns;
                    back[i, j] = OpInsertion;
                }
            }
        }

        var reversed = new List<AlignmentStep>(m + n);
        int ci = m;
        int cj = n;
        while (ci > 0 || cj > 0)
        {
            byte op = back[ci, cj];
            if (ci > 0 && cj > 0 && op == OpPair)
            {
                reversed.Add(new AlignmentStep(AlignmentStepKind.Pair, ci - 1, cj - 1));
                ci--;
                cj--;
            }
            else if (ci > 0 && (cj == 0 || op == OpDeletion))
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

    internal static List<AlignmentStep> BuildUnalignedObservedSteps(int boxCount)
    {
        var steps = new List<AlignmentStep>(boxCount);
        for (int j = 0; j < boxCount; j++)
        {
            steps.Add(new AlignmentStep(AlignmentStepKind.Pair, j, j));
        }

        return steps;
    }

    internal static int EstimateDeletedSlotColumn(
        List<AlignmentStep> steps,
        int stepIndex,
        ReadOnlySpan<ObservedBoxSummary> obsBoxes,
        int stripW)
    {
        for (int p = stepIndex - 1; p >= 0; p--)
        {
            if (steps[p].BoxIndex >= 0)
            {
                return Math.Min(stripW - 1, obsBoxes[steps[p].BoxIndex].Box.EndCol + 1);
            }
        }

        for (int q = stepIndex + 1; q < steps.Count; q++)
        {
            if (steps[q].BoxIndex >= 0)
            {
                return Math.Max(0, obsBoxes[steps[q].BoxIndex].Box.StartCol);
            }
        }

        return stripW / 2;
    }

    private static float ComputeSlotBoxPairCost(
        IndustrialOcrFontModel goldenFont,
        int slotIdx,
        char? expectedChar,
        bool hasExpectedText,
        ReadOnlySpan<float> candGrid,
        ReadOnlySpan<float> candCl2n,
        ReadOnlySpan<float> candQuads,
        OcrFieldFormatSpec spec,
        OcvVerificationOptions options)
    {
        OcvTemplateMatcher.TemplateMatchOutcome slotMatch = OcvTemplateMatcher.MatchSlotWithFullTemplateMargin(
            goldenFont,
            candGrid,
            candCl2n,
            candQuads,
            slotIdx,
            spec);

        if (hasExpectedText && expectedChar.HasValue)
        {
            if (slotMatch.BestTemplate is not null && slotMatch.BestChar == expectedChar.Value)
            {
                return AlignmentMatchDissimilarityWeight * (1.0f - slotMatch.BestScore);
            }

            float expectedSim = OcvTemplateMatcher.ComputeBestTemplateScoreForChar(
                goldenFont,
                expectedChar.Value,
                candGrid,
                candCl2n,
                candQuads);
            return AlignmentSubstitutionBaseCost + (AlignmentMatchDissimilarityWeight * (1.0f - expectedSim));
        }

        bool allowed = slotMatch.BestTemplate is not null && spec.AllowsCharacterAt(slotIdx, slotMatch.BestChar);
        if (allowed && slotMatch.BestScore >= options.ReviewMatchScore)
        {
            return AlignmentMatchDissimilarityWeight * (1.0f - slotMatch.BestScore);
        }

        return AlignmentSubstitutionBaseCost + (AlignmentMatchDissimilarityWeight * (1.0f - slotMatch.BestScore));
    }
}
