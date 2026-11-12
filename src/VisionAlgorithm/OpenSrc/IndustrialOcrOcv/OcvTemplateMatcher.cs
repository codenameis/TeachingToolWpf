using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace VisionAlgorithm.OpenSrc.IndustrialOcrOcv;

/// <summary>
/// Internal template matching and shift-tolerant stroke defect evaluator for <see cref="TaughtFontOcvVerifier"/>.
/// Computes confusion margin across ALL golden templates (never masked to allowed-only templates) so confusable
/// out-of-format characters (<c>B↔8</c>, <c>O↔0</c>, <c>I↔1</c>, <c>S↔5</c>) cannot hide behind field format masks.
/// </summary>
internal static class OcvTemplateMatcher
{
    internal const char UnmatchedRejectSymbol = '?';

    private const float GoldenStrokeActiveFloor = 0.45f;
    private const float CandidateStrokePresentFloor = 0.22f;
    private const float GoldenBackgroundSafeCeiling = 0.12f;
    private const float CandidateBleedInkFloor = 0.48f;
    private const float Cl2nScoreWeight = 0.45f;
    private const float DirectGridScoreWeight = 0.30f;
    private const float QuadrantScoreWeight = 0.25f;
    private const float QuadrantMaxDiffWeight = 0.55f;
    private const float QuadrantMeanDiffWeight = 0.45f;
    private const float CosineNormalizationScale = 0.5f;
    private const float CosineNormalizationBias = 0.5f;

    internal readonly record struct TemplateMatchOutcome(
        char BestChar,
        float BestScore,
        float FullTemplateConfusionMargin,
        IndustrialOcrGlyphTemplate? BestTemplate);

    internal static TemplateMatchOutcome MatchUnconstrained(
        IndustrialOcrFontModel goldenFont,
        ReadOnlySpan<float> candGrid,
        ReadOnlySpan<float> candCl2n,
        ReadOnlySpan<float> candQuads)
    {
        char bestChar = UnmatchedRejectSymbol;
        float bestScore = 0f;
        float runnerUpScore = 0f;
        IndustrialOcrGlyphTemplate? bestTemplate = null;

        ReadOnlySpan<IndustrialOcrGlyphTemplate> templates = goldenFont.Templates;
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

        float margin = bestTemplate is not null
            ? MathF.Max(0f, bestScore - runnerUpScore)
            : 0f;
        return new TemplateMatchOutcome(bestChar, bestScore, margin, bestTemplate);
    }

    internal static TemplateMatchOutcome MatchSlotWithFullTemplateMargin(
        IndustrialOcrFontModel goldenFont,
        ReadOnlySpan<float> candGrid,
        ReadOnlySpan<float> candCl2n,
        ReadOnlySpan<float> candQuads,
        int charIndex,
        OcrFieldFormatSpec fieldSpec)
    {
        char constrainedBestChar = UnmatchedRejectSymbol;
        float constrainedBestScore = 0f;
        IndustrialOcrGlyphTemplate? constrainedBestTemplate = null;

        ReadOnlySpan<IndustrialOcrGlyphTemplate> templates = goldenFont.Templates;
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
                constrainedBestScore = score;
                constrainedBestChar = tpl.Character;
                constrainedBestTemplate = tpl;
            }
        }

        if (constrainedBestTemplate is null)
        {
            return new TemplateMatchOutcome(UnmatchedRejectSymbol, 0f, 0f, null);
        }

        // [Math Why]: Compute runner-up across ALL templates in the font (not just mask-allowed templates)
        // so that an out-of-mask confusable glyph (e.g., 'B' printed in a 'D' digit slot) produces zero/negative
        // margin relative to '8' instead of comparing '8' only against '0'..'9'.
        float allTemplateRunnerUpScore = 0f;
        for (int t = 0; t < templates.Length; t++)
        {
            IndustrialOcrGlyphTemplate tpl = templates[t];
            if (tpl.Character == constrainedBestChar)
            {
                continue;
            }

            float score = ComputeCombinedTemplateScore(candGrid, candCl2n, candQuads, tpl);
            if (score > allTemplateRunnerUpScore)
            {
                allTemplateRunnerUpScore = score;
            }
        }

        float fullConfusionMargin = MathF.Max(0f, constrainedBestScore - allTemplateRunnerUpScore);
        return new TemplateMatchOutcome(
            constrainedBestChar,
            constrainedBestScore,
            fullConfusionMargin,
            constrainedBestTemplate);
    }

    internal static float ComputeBestTemplateScoreForChar(
        IndustrialOcrFontModel goldenFont,
        char targetChar,
        ReadOnlySpan<float> candGrid,
        ReadOnlySpan<float> candCl2n,
        ReadOnlySpan<float> candQuads)
    {
        float bestScore = 0f;
        ReadOnlySpan<IndustrialOcrGlyphTemplate> templates = goldenFont.Templates;
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

    internal static IndustrialOcrGlyphTemplate? SelectStrokeReferenceTemplate(
        IndustrialOcrFontModel goldenFont,
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
        ReadOnlySpan<IndustrialOcrGlyphTemplate> templates = goldenFont.Templates;
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

    internal static (float MissingStrokeRatio, float BleedRatio) ComputeShiftTolerantStrokeMetrics(
        ReadOnlySpan<float> candidateGrid,
        ReadOnlySpan<float> goldenGrid)
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
                            if (v > maxCandNeighborhood)
                            {
                                maxCandNeighborhood = v;
                            }
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
                        if (v > maxGoldenNeighborhood)
                        {
                            maxGoldenNeighborhood = v;
                        }
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

        float missingStrokeRatio = goldenActiveCount > 0
            ? (float)missingActiveCount / goldenActiveCount
            : 0f;
        float bleedRatio = goldenSafeBgCount > 0
            ? (float)bleedBgCount / goldenSafeBgCount
            : 0f;
        return (missingStrokeRatio, bleedRatio);
    }

    internal static float ComputeCombinedTemplateScore(
        ReadOnlySpan<float> candGrid,
        ReadOnlySpan<float> candCl2n,
        ReadOnlySpan<float> candQuads,
        IndustrialOcrGlyphTemplate tpl)
    {
        float dot = ComputeDotProductSimd(candCl2n, tpl.Cl2nFeature);
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static float ComputeDotProductSimd(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        int length = Math.Min(a.Length, b.Length);
        int i = 0;
        float sum = 0f;
        int laneCount = Vector256<float>.Count;

        if (Avx2.IsSupported && length >= laneCount)
        {
            Vector256<float> acc = Vector256<float>.Zero;
            ref float aRef = ref MemoryMarshal.GetReference(a);
            ref float bRef = ref MemoryMarshal.GetReference(b);
            int simdLimit = length & ~(laneCount - 1);

            for (; i < simdLimit; i += laneCount)
            {
                Vector256<float> va = Vector256.LoadUnsafe(ref Unsafe.Add(ref aRef, i));
                Vector256<float> vb = Vector256.LoadUnsafe(ref Unsafe.Add(ref bRef, i));
                acc = Avx.Add(acc, Avx.Multiply(va, vb));
            }

            Vector128<float> sum128 = Sse.Add(acc.GetLower(), acc.GetUpper());
            Vector128<float> shuf = Sse.Shuffle(sum128, sum128, 0b_01_00_11_10);
            sum128 = Sse.Add(sum128, shuf);
            shuf = Sse.Shuffle(sum128, sum128, 0b_00_00_00_01);
            sum = Sse.AddScalar(sum128, shuf).ToScalar();
        }

        for (; i < length; i++)
        {
            sum += a[i] * b[i];
        }

        return sum;
    }
}
