using System;
using System.Buffers;
using VisionAlgorithm.OpenSrc.Common;

namespace VisionOcr.PpOcr;

/// <summary>
/// Scale-invariant layout complexity routing, border-luminance polarity prior estimation,
/// and confidence-weighted polarity/multilingual candidate selection.
/// </summary>
internal static class PpOcrPolarityRouter
{
    public const float VerticalAspectTransposeRatio = 1.50f;
    public const float HighConfidenceShortCircuit = 0.910f;
    public const float SingleLineAcceptShortCircuit = 0.850f;
    public const float UpsideDownCheckThreshold = 0.720f;

    private const int MinTwoLineRowsForNyquist = 12;
    private const int MinFloorBandRows = 2;
    private const int MinFloorGapRows = 2;
    private const float MultiLineTallAspectRatio = 1.25f;
    private const float MultiLineCompactAspectRatio = 2.60f;
    private const float MinRowEdgeEnergy = 4.0f;
    private const float BandEnergyRatio = 0.25f;
    private const float RelativeMinBandHeightRatio = 0.08f;
    private const float RelativeMinBandGapRatio = 0.05f;
    private const float DarkBackgroundBorderMeanCutoff = 110.0f;
    private const float InteractiveEnglishShortCircuit = 0.880f;
    private const float MeanConfidenceScoreWeight = 0.80f;
    private const float MinConfidenceScoreWeight = 0.20f;
    private const float PolaritySwitchScoreMargin = 0.015f;
    private const float HangulSelectionMargin = 0.010f;
    private const float NonHangulSelectionMargin = 0.055f;
    private const float GuaranteedEnglishWinScore = 1.0f - HangulSelectionMargin;

    /// <summary>
    /// Detects multi-line or vertical layouts using scale-invariant aspect ratio and relative horizontal edge-energy bands
    /// (zero hardcoded image/ROI pixel thresholds like 52px or 34px).
    /// </summary>
    public static bool IsLikelyMultiLineOrVerticalLayout(ReadOnlyImageView2D<byte> roiView)
    {
        int width = roiView.Width;
        int height = roiView.Height;
        if (height >= width * MultiLineTallAspectRatio)
        {
            return true;
        }

        float aspectRatio = width / (float)Math.Max(1, height);
        if (aspectRatio < MultiLineCompactAspectRatio)
        {
            return true;
        }

        if (height < MinTwoLineRowsForNyquist)
        {
            return false;
        }

        int minBandRows = Math.Max(MinFloorBandRows, (int)MathF.Round(height * RelativeMinBandHeightRatio));
        int minGapRows = Math.Max(MinFloorGapRows, (int)MathF.Round(height * RelativeMinBandGapRatio));

        float[] rowEnergy = ArrayPool<float>.Shared.Rent(height);
        try
        {
            Span<float> energy = rowEnergy.AsSpan(0, height);
            energy.Clear();
            float maxEnergy = 0f;
            ReadOnlySpan<byte> px = roiView.Pixels;
            int stride = roiView.Stride;

            for (int y = 0; y < height; y++)
            {
                ReadOnlySpan<byte> row = px.Slice(y * stride, width);
                int diffSum = 0;
                for (int x = 1; x < width; x++)
                {
                    diffSum += Math.Abs(row[x] - row[x - 1]);
                }

                float e = diffSum / (float)Math.Max(1, width - 1);
                energy[y] = e;
                if (e > maxEnergy)
                {
                    maxEnergy = e;
                }
            }

            if (maxEnergy < MinRowEdgeEnergy)
            {
                return false;
            }

            float bandThreshold = maxEnergy * BandEnergyRatio;
            int bandCount = 0;
            bool inBand = false;
            int bandStart = 0;
            int gapRows = 0;

            for (int y = 0; y < height; y++)
            {
                if (energy[y] >= bandThreshold)
                {
                    if (!inBand)
                    {
                        if (bandCount == 0 || gapRows >= minGapRows)
                        {
                            bandStart = y;
                        }

                        inBand = true;
                    }

                    gapRows = 0;
                }
                else if (inBand)
                {
                    if ((y - bandStart) >= minBandRows)
                    {
                        bandCount++;
                    }

                    inBand = false;
                    gapRows = 1;
                }
                else
                {
                    gapRows++;
                }
            }

            if (inBand && (height - bandStart) >= minBandRows)
            {
                bandCount++;
            }

            return bandCount >= 2;
        }
        finally
        {
            ArrayPool<float>.Shared.Return(rowEnergy);
        }
    }

    public static bool EstimateIsLightOnDark(ReadOnlyImageView2D<byte> view)
    {
        int width = view.Width;
        int height = view.Height;
        ReadOnlySpan<byte> px = view.Pixels;
        int stride = view.Stride;

        long borderSum = 0;
        int borderCount = 0;
        ReadOnlySpan<byte> top = px.Slice(0, width);
        ReadOnlySpan<byte> bot = px.Slice((height - 1) * stride, width);
        for (int x = 0; x < width; x++)
        {
            borderSum += top[x] + bot[x];
            borderCount += 2;
        }

        for (int y = 1; y < height - 1; y++)
        {
            ReadOnlySpan<byte> row = px.Slice(y * stride, width);
            borderSum += row[0] + row[width - 1];
            borderCount += 2;
        }

        float borderMean = borderSum / (float)Math.Max(1, borderCount);
        return borderMean < DarkBackgroundBorderMeanCutoff;
    }

    public static ReadOnlySpan<bool> ResolvePolarityOrder(PpOcrPolarityMode mode, bool darkBgPrior, bool fastInteractivePreview)
    {
        if (mode == PpOcrPolarityMode.NormalOnly)
        {
            return [false];
        }

        if (mode == PpOcrPolarityMode.InvertedOnly)
        {
            return [true];
        }

        if (fastInteractivePreview)
        {
            return darkBgPrior ? [true] : [false];
        }

        return darkBgPrior ? [true, false] : [false, true];
    }

    public static bool ShouldShortCircuitEnglishLine(PpOcrCtcDecoder.DecodedCropLine enLine, bool fastInteractivePreview)
    {
        if (enLine.Characters.Count == 0)
        {
            return false;
        }

        if (fastInteractivePreview && enLine.MeanConfidence >= InteractiveEnglishShortCircuit)
        {
            return true;
        }

        float enScore = (enLine.MeanConfidence * MeanConfidenceScoreWeight) + (enLine.MinConfidence * MinConfidenceScoreWeight);
        return enScore >= GuaranteedEnglishWinScore;
    }

    public static PpOcrRecognitionResult SelectHigherConfidenceResult(
        PpOcrRecognitionResult first,
        PpOcrRecognitionResult second)
    {
        if (first.Region.Characters.Count == 0)
        {
            return second;
        }

        if (second.Region.Characters.Count == 0)
        {
            return first;
        }

        float score1 = (first.Region.MeanConfidence * MeanConfidenceScoreWeight) + (first.Region.MinConfidence * MinConfidenceScoreWeight);
        float score2 = (second.Region.MeanConfidence * MeanConfidenceScoreWeight) + (second.Region.MinConfidence * MinConfidenceScoreWeight);
        return score2 > score1 + PolaritySwitchScoreMargin ? second : first;
    }

    public static PpOcrCtcDecoder.DecodedCropLine SelectBetterCropLine(
        PpOcrCtcDecoder.DecodedCropLine enLine,
        PpOcrCtcDecoder.DecodedCropLine koLine)
    {
        if (enLine.Characters.Count == 0)
        {
            return koLine;
        }

        if (koLine.Characters.Count == 0)
        {
            return enLine;
        }

        float enScore = (enLine.MeanConfidence * MeanConfidenceScoreWeight) + (enLine.MinConfidence * MinConfidenceScoreWeight);
        float koScore = (koLine.MeanConfidence * MeanConfidenceScoreWeight) + (koLine.MinConfidence * MinConfidenceScoreWeight);
        bool koHasHangul = ContainsHangul(koLine.Text);
        float margin = koHasHangul ? HangulSelectionMargin : NonHangulSelectionMargin;
        return koScore > enScore + margin ? koLine : enLine;
    }

    private static bool ContainsHangul(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if ((c >= '\uAC00' && c <= '\uD7A3') || (c >= '\u3131' && c <= '\u318E'))
            {
                return true;
            }
        }

        return false;
    }
}
