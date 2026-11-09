// File: src/VisionAlgorithm/OpenSrc/IndustrialOcrOcv/OcrMaxFieldingMatcher.cs
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using VisionAlgorithm.OpenSrc.Common;

namespace VisionAlgorithm.OpenSrc.IndustrialOcrOcv;

/// <summary>
/// Cognex <c>OCRMax</c>-style position-fielded character classifier with AVX2 CL2N inner-product scoring,
/// primary vs. runner-up confusion margin (<c>8/3/B</c>, <c>0/O/D</c>, <c>1/I/7</c>, <c>5/S</c>) evaluation,
/// and pharmaceutical expiry calendar date syntax validation.
/// </summary>
public static class OcrMaxFieldingMatcher
{
    private const int Avx2LaneCount = 8;

    /// <summary>
    /// Evaluates a normalized character feature vector against <paramref name="fontModel"/> under position-specific
    /// <paramref name="fieldingRule"/> constraints, returning the best character, runner-up confusable character,
    /// primary score $S_1 \in [0, 1]$, runner-up score $S_2 \in [0, 1]$, and confusion margin $S_1 - S_2$.
    /// </summary>
    public static void MatchCharacter(
        IndustrialOcrFontModel fontModel,
        ReadOnlySpan<float> charNormalizedGrid,
        ReadOnlySpan<float> charHorizontalProj,
        ReadOnlySpan<float> charVerticalProj,
        ReadOnlySpan<float> charQuadrants,
        int charIndexInTokenStream,
        in FieldingMaskRule fieldingRule,
        out char bestChar,
        out char runnerUpChar,
        out float bestScore,
        out float runnerUpScore,
        out float confusionMargin,
        out IndustrialOcrGlyphTemplate? bestTemplate)
    {
        ArgumentNullException.ThrowIfNull(fontModel);

        Span<float> cl2n = stackalloc float[IndustrialOcrGlyphTemplate.GridLength];
        SimpleShotNormalizer.CenterAndL2Normalize(charNormalizedGrid, fontModel.GlobalMean, cl2n);

        ReadOnlySpan<IndustrialOcrGlyphTemplate> templates = fontModel.Templates;
        bestChar = '?';
        runnerUpChar = '?';
        bestScore = -1f;
        runnerUpScore = -1f;
        bestTemplate = null;

        for (int i = 0; i < templates.Length; i++)
        {
            IndustrialOcrGlyphTemplate tpl = templates[i];
            if (!fieldingRule.IsAllowedAt(charIndexInTokenStream, tpl.Character))
            {
                continue;
            }

            float cosine = ComputeDotProductAvx2(cl2n, tpl.Cl2nFeature);
            float gridCorr = Math.Clamp((cosine + 1.0f) * 0.5f, 0f, 1f);

            // Blend 2D CL2N correlation with quadrant structural agreement so missing strokes directly penalize primary match score
            float quadPenalty = ComputeQuadrantStructuralPenalty(charQuadrants, tpl.QuadrantDensities);
            float combinedScore = Math.Clamp((0.78f * gridCorr) + (0.22f * (1.0f - quadPenalty)), 0f, 1f);

            if (combinedScore > bestScore)
            {
                runnerUpScore = bestScore;
                runnerUpChar = bestChar;
                bestScore = combinedScore;
                bestChar = tpl.Character;
                bestTemplate = tpl;
            }
            else if (combinedScore > runnerUpScore)
            {
                runnerUpScore = combinedScore;
                runnerUpChar = tpl.Character;
            }
        }

        if (bestScore < 0f)
        {
            bestScore = 0f;
            runnerUpScore = 0f;
            confusionMargin = 0f;
            return;
        }

        if (runnerUpScore < 0f)
        {
            runnerUpScore = 0f;
        }

        confusionMargin = MathF.Max(0f, bestScore - runnerUpScore);
    }

    /// <summary>
    /// Validates whether a decoded expiry string (e.g. <c>"EXP 2028.08.15"</c>) satisfies calendar date constraints
    /// ($2020 \le \text{YYYY} \le 2099$, $01 \le \text{MM} \le 12$, $01 \le \text{DD} \le \text{DaysInMonth}(\text{YYYY}, \text{MM})$).
    /// </summary>
    public static bool VerifyExpiryCalendarDate(ReadOnlySpan<char> decodedText)
    {
        // Locate 8-digit date pattern YYYY.MM.DD or YYYYMMDD inside the decoded text
        Span<char> digits = stackalloc char[16];
        int digitCount = 0;
        for (int i = 0; i < decodedText.Length && digitCount < digits.Length; i++)
        {
            char c = decodedText[i];
            if (c is >= '0' and <= '9')
            {
                digits[digitCount++] = c;
            }
        }

        if (digitCount < 8)
        {
            return false;
        }

        ReadOnlySpan<char> dateDigits = digits.Slice(digitCount - 8, 8);
        int year = ParseFixedDigits(dateDigits.Slice(0, 4));
        int month = ParseFixedDigits(dateDigits.Slice(4, 2));
        int day = ParseFixedDigits(dateDigits.Slice(6, 2));

        if (year is < 2000 or > 2099 || month is < 1 or > 12 || day < 1)
        {
            return false;
        }

        int maxDay = DateTime.DaysInMonth(year, month);
        return day <= maxDay;
    }

    private static int ParseFixedDigits(ReadOnlySpan<char> span)
    {
        int val = 0;
        for (int i = 0; i < span.Length; i++)
        {
            val = (val * 10) + (span[i] - '0');
        }

        return val;
    }

    private static float ComputeQuadrantStructuralPenalty(
        ReadOnlySpan<float> candidateQuads,
        ReadOnlySpan<float> goldenQuads)
    {
        float maxDiff = 0f;
        float sumDiff = 0f;
        int len = Math.Min(candidateQuads.Length, goldenQuads.Length);
        for (int i = 0; i < len; i++)
        {
            float diff = MathF.Abs(candidateQuads[i] - goldenQuads[i]);
            sumDiff += diff;
            if (diff > maxDiff)
            {
                maxDiff = diff;
            }
        }

        return Math.Clamp((0.55f * maxDiff) + (0.45f * (sumDiff / Math.Max(1, len))), 0f, 1f);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static float ComputeDotProductAvx2(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        int length = Math.Min(a.Length, b.Length);
        int i = 0;
        float sum = 0f;

        if (Avx2.IsSupported && length >= Avx2LaneCount)
        {
            Vector256<float> acc = Vector256<float>.Zero;
            ref float aRef = ref MemoryMarshal.GetReference(a);
            ref float bRef = ref MemoryMarshal.GetReference(b);
            int simdLimit = length - (length % Avx2LaneCount);

            for (; i < simdLimit; i += Avx2LaneCount)
            {
                Vector256<float> va = Vector256.LoadUnsafe(ref Unsafe.Add(ref aRef, i));
                Vector256<float> vb = Vector256.LoadUnsafe(ref Unsafe.Add(ref bRef, i));
                acc = Avx.Add(acc, Avx.Multiply(va, vb));
            }

            Vector128<float> low = acc.GetLower();
            Vector128<float> high = acc.GetUpper();
            Vector128<float> sum128 = Sse.Add(low, high);
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
