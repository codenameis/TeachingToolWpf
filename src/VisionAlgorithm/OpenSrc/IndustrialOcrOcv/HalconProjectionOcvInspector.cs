// File: src/VisionAlgorithm/OpenSrc/IndustrialOcrOcv/HalconProjectionOcvInspector.cs
using VisionAlgorithm.OpenSrc.Common;

namespace VisionAlgorithm.OpenSrc.IndustrialOcrOcv;

/// <summary>
/// MVTec HALCON <c>do_ocv_simple</c>-inspired Optical Character Verification (OCV) inspector combining
/// <c>AdaptGray</c> contrast validation, <c>AdaptPos</c> sub-pixel alignment (<see cref="SubpixelInterpolator.InterpolateParabolicPeak"/>),
/// 1D horizontal/vertical projection profile correlation, $4 \times 4$ quadrant missing-stroke detection, and inter-character ink smear checks.
/// </summary>
public static class HalconProjectionOcvInspector
{
    private const float ActiveGoldenQuadrantFloor = 0.26f;
    private const float CorrelationEpsilon = 1e-6f;

    /// <summary>
    /// Verifies the structural print quality of a segmented character patch against its matched <see cref="IndustrialOcrGlyphTemplate"/>.
    /// </summary>
    public static void InspectCharacterQuality(
        ReadOnlyImageView2D<byte> rectifiedStrip,
        in SegmentedCharBox charBox,
        IndustrialOcrGlyphTemplate goldenTemplate,
        ReadOnlySpan<float> candidateHorizontalProj,
        ReadOnlySpan<float> candidateVerticalProj,
        ReadOnlySpan<float> candidateQuadrants,
        float contrastRange,
        in HalconOcvParams ocvParams,
        out float ocvQualityScore,
        out float worstQuadrantRetention,
        out float subpixelShiftX,
        out float subpixelShiftY,
        out OcvDefectFlags defectFlags)
    {
        ArgumentNullException.ThrowIfNull(goldenTemplate);
        defectFlags = OcvDefectFlags.None;

        // 1. AdaptGray contrast check
        if (contrastRange < ocvParams.MinContrast)
        {
            defectFlags |= OcvDefectFlags.LowContrast;
        }

        // 2. AdaptPos sub-pixel shift estimation along X and Y projection correlations (-1, 0, +1 shift)
        float corrXMinus = ComputeShiftedCorrelation(candidateVerticalProj, goldenTemplate.VerticalProjection, -1);
        float corrXZero = ComputeShiftedCorrelation(candidateVerticalProj, goldenTemplate.VerticalProjection, 0);
        float corrXPlus = ComputeShiftedCorrelation(candidateVerticalProj, goldenTemplate.VerticalProjection, +1);

        float corrYMinus = ComputeShiftedCorrelation(candidateHorizontalProj, goldenTemplate.HorizontalProjection, -1);
        float corrYZero = ComputeShiftedCorrelation(candidateHorizontalProj, goldenTemplate.HorizontalProjection, 0);
        float corrYPlus = ComputeShiftedCorrelation(candidateHorizontalProj, goldenTemplate.HorizontalProjection, +1);

        if (ocvParams.AdaptPos)
        {
            subpixelShiftX = SubpixelInterpolator.InterpolateParabolicPeak(corrXMinus, corrXZero, corrXPlus, out float peakCorrX);
            subpixelShiftY = SubpixelInterpolator.InterpolateParabolicPeak(corrYMinus, corrYZero, corrYPlus, out float peakCorrY);
            corrXZero = Math.Clamp(peakCorrX, 0f, 1f);
            corrYZero = Math.Clamp(peakCorrY, 0f, 1f);
        }
        else
        {
            subpixelShiftX = 0f;
            subpixelShiftY = 0f;
        }

        float projCorrelation = 0.5f * (corrXZero + corrYZero);

        // 3. 2x2 Macro-Quadrant & 4x4 Quadrant Stroke Coverage Integrity Check (detects missing dots/broken strokes)
        worstQuadrantRetention = 1.0f;
        float excessSmearQuadrant = 0.0f;
        ReadOnlySpan<float> goldenQuads = goldenTemplate.QuadrantDensities;

        // Evaluate 2x2 macro-quadrants (Top-Left, Top-Right, Bottom-Left, Bottom-Right) for translation/rotation robustness
        Span<float> goldenMacro = stackalloc float[4];
        Span<float> candMacro = stackalloc float[4];
        goldenMacro.Clear();
        candMacro.Clear();

        for (int qr = 0; qr < IndustrialOcrGlyphTemplate.QuadrantRows; qr++)
        {
            int mr = qr >> 1;
            for (int qc = 0; qc < IndustrialOcrGlyphTemplate.QuadrantCols; qc++)
            {
                int mc = qc >> 1;
                int q = (qr * IndustrialOcrGlyphTemplate.QuadrantCols) + qc;
                int mIdx = (mr * 2) + mc;
                goldenMacro[mIdx] += goldenQuads[q] * 0.25f;
                candMacro[mIdx] += candidateQuadrants[q] * 0.25f;

                if (goldenQuads[q] < ActiveGoldenQuadrantFloor)
                {
                    float excess = candidateQuadrants[q] - goldenQuads[q];
                    if (excess > excessSmearQuadrant)
                    {
                        excessSmearQuadrant = excess;
                    }
                }
            }
        }

        for (int m = 0; m < 4; m++)
        {
            if (goldenMacro[m] >= ActiveGoldenQuadrantFloor * 0.65f)
            {
                float retention = candMacro[m] / goldenMacro[m];
                if (retention < worstQuadrantRetention)
                {
                    worstQuadrantRetention = retention;
                }
            }
        }

        float maxAllowedRetentionFloor = 1.0f - ocvParams.MaxQuadrantDropRatio;
        if (worstQuadrantRetention < maxAllowedRetentionFloor)
        {
            defectFlags |= OcvDefectFlags.MissingStrokeOrDot;
        }

        // 4. Ink Smear / Bleed check: inter-character bridge density OR background margin contamination
        float borderContamination = ComputeMarginContamination(rectifiedStrip, in charBox);
        if (charBox.InterCharBridgeDensity >= ocvParams.MaxMarginSmearRatio ||
            borderContamination >= ocvParams.MaxMarginSmearRatio ||
            excessSmearQuadrant >= (ocvParams.MaxMarginSmearRatio * 1.35f))
        {
            defectFlags |= OcvDefectFlags.InkSmear;
        }

        ocvQualityScore = Math.Clamp(
            (0.55f * projCorrelation) + (0.45f * Math.Clamp(worstQuadrantRetention, 0f, 1f)) - (0.35f * borderContamination),
            0f,
            1f);

        if (ocvQualityScore < ocvParams.MinProjectionCorrelation)
        {
            defectFlags |= OcvDefectFlags.LowAcceptanceScore;
        }
    }

    private static float ComputeShiftedCorrelation(
        ReadOnlySpan<float> a,
        ReadOnlySpan<float> b,
        int shift)
    {
        int len = Math.Min(a.Length, b.Length);
        float meanA = 0f;
        float meanB = 0f;
        int count = 0;

        for (int i = 0; i < len; i++)
        {
            int j = i + shift;
            if (j < 0 || j >= len)
            {
                continue;
            }

            meanA += a[i];
            meanB += b[j];
            count++;
        }

        if (count == 0)
        {
            return 0f;
        }

        meanA /= count;
        meanB /= count;

        float num = 0f;
        float denA = 0f;
        float denB = 0f;

        for (int i = 0; i < len; i++)
        {
            int j = i + shift;
            if (j < 0 || j >= len)
            {
                continue;
            }

            float da = a[i] - meanA;
            float db = b[j] - meanB;
            num += da * db;
            denA += da * da;
            denB += db * db;
        }

        float denom = MathF.Sqrt(denA * denB);
        if (denom <= CorrelationEpsilon)
        {
            return 0f;
        }

        return Math.Clamp((num / denom + 1.0f) * 0.5f, 0f, 1f);
    }

    private static float ComputeMarginContamination(
        ReadOnlyImageView2D<byte> rectifiedStrip,
        in SegmentedCharBox box)
    {
        int w = rectifiedStrip.Width;
        int h = rectifiedStrip.Height;

        // Measure dark ink presence in the upper/lower quiet zones above and below the character box
        int quietRows = 0;
        int contaminatedPixels = 0;
        int totalQuietPixels = 0;

        float bgSum = 0f;
        int bgCount = 0;
        ReadOnlySpan<byte> topRowSpan = rectifiedStrip.RowSpan(0);
        for (int c = 0; c < w; c++)
        {
            bgSum += topRowSpan[c];
            bgCount++;
        }

        float bgMean = bgCount > 0 ? bgSum / bgCount : 220f;
        float inkDropThreshold = 38.0f;

        int topQuietEnd = Math.Max(0, box.TopRow - 2);
        for (int r = 0; r < topQuietEnd; r++)
        {
            quietRows++;
            ReadOnlySpan<byte> row = rectifiedStrip.RowSpan(r);
            for (int c = box.StartCol; c <= box.EndCol; c++)
            {
                totalQuietPixels++;
                if ((bgMean - row[c]) >= inkDropThreshold)
                {
                    contaminatedPixels++;
                }
            }
        }

        int botQuietStart = Math.Min(h - 1, box.BottomRow + 2);
        for (int r = botQuietStart; r < h; r++)
        {
            quietRows++;
            ReadOnlySpan<byte> row = rectifiedStrip.RowSpan(r);
            for (int c = box.StartCol; c <= box.EndCol; c++)
            {
                totalQuietPixels++;
                if ((bgMean - row[c]) >= inkDropThreshold)
                {
                    contaminatedPixels++;
                }
            }
        }

        if (quietRows == 0 || totalQuietPixels == 0)
        {
            return box.InterCharBridgeDensity;
        }

        float quietRatio = (float)contaminatedPixels / totalQuietPixels;
        return MathF.Max(quietRatio, box.InterCharBridgeDensity);
    }
}
