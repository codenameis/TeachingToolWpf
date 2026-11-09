// File: src/VisionAlgorithm/OpenSrc/IndustrialOcrOcv/IndustrialOcrOcvEngine.cs
using System.Text;
using VisionAlgorithm.OpenSrc.Common;

namespace VisionAlgorithm.OpenSrc.IndustrialOcrOcv;

/// <summary>
/// Unified end-to-end industrial OCR + OCV execution pipeline combining <see cref="OcrMaxSegmenter"/>,
/// <see cref="OcrMaxFieldingMatcher"/>, pharmaceutical calendar date verification, and <see cref="HalconProjectionOcvInspector"/>.
/// </summary>
public static class IndustrialOcrOcvEngine
{
    private const int MaxLineCharacters = 64;

    /// <summary>
    /// Executes segmentation, fielded OCR matching, calendar syntax validation, and projection/quadrant OCV verification
    /// on a raw 8-bit image buffer anchored by <paramref name="fixture"/>.
    /// </summary>
    public static OcrOcvLineResult InspectLine(
        ReadOnlySpan<byte> image,
        int width,
        int height,
        int stride,
        in FixturePose2D fixture,
        IndustrialOcrFontModel fontModel,
        in OcrMaxSegmentParams segmentParams,
        in HalconOcvParams ocvParams,
        in FieldingMaskRule fieldingRule,
        VisionWorkspace workspace)
    {
        ReadOnlyImageView2D<byte> view = new(image, width, height, stride);
        return InspectLine(view, in fixture, fontModel, in segmentParams, in ocvParams, in fieldingRule, workspace);
    }

    /// <summary>
    /// Executes segmentation, fielded OCR matching, calendar syntax validation, and projection/quadrant OCV verification
    /// on a <see cref="ReadOnlyImageView2D{T}"/> anchored by <paramref name="fixture"/>.
    /// </summary>
    public static OcrOcvLineResult InspectLine(
        ReadOnlyImageView2D<byte> image,
        in FixturePose2D fixture,
        IndustrialOcrFontModel fontModel,
        in OcrMaxSegmentParams segmentParams,
        in HalconOcvParams ocvParams,
        in FieldingMaskRule fieldingRule,
        VisionWorkspace workspace)
    {
        image.EnsureValid();
        fixture.EnsureValid();
        ArgumentNullException.ThrowIfNull(fontModel);
        ArgumentNullException.ThrowIfNull(workspace);

        int stripW = segmentParams.RectifiedWidth;
        int stripH = segmentParams.RectifiedHeight;
        Span<byte> rectifiedStrip = workspace.GetByteBufferA(checked(stripW * stripH));
        Span<SegmentedCharBox> boxes = stackalloc SegmentedCharBox[MaxLineCharacters];

        int boxCount = OcrMaxSegmenter.SegmentLine(
            image,
            in fixture,
            in segmentParams,
            workspace,
            rectifiedStrip,
            boxes);

        if (boxCount == 0)
        {
            return new OcrOcvLineResult(
                string.Empty,
                IsPass: false,
                OcvDefectFlags.SegmentationCountMismatch | OcvDefectFlags.LowContrast,
                MinCharScore: 0f,
                MinConfusionMargin: 0f,
                MinOcvQualityScore: 0f,
                MinContrast: 0f,
                []);
        }

        ReadOnlyImageView2D<byte> stripView = new(rectifiedStrip, stripW, stripH, stripW);
        var charResults = new CharVerifyResult[boxCount];
        var sb = new StringBuilder(boxCount + 4);

        Span<float> charGrid = stackalloc float[IndustrialOcrGlyphTemplate.GridLength];
        Span<float> charHProj = stackalloc float[IndustrialOcrGlyphTemplate.GridHeight];
        Span<float> charVProj = stackalloc float[IndustrialOcrGlyphTemplate.GridWidth];
        Span<float> charQuads = stackalloc float[IndustrialOcrGlyphTemplate.QuadrantCount];

        OcvDefectFlags lineFlags = OcvDefectFlags.None;
        float minCharScore = 1.0f;
        float minConfusionMargin = 1.0f;
        float minOcvQuality = 1.0f;
        float minContrast = float.MaxValue;

        for (int i = 0; i < boxCount; i++)
        {
            SegmentedCharBox box = boxes[i];
            if (box.PrecededBySpace && sb.Length > 0)
            {
                sb.Append(' ');
            }

            ReadOnlySpan<byte> patchSlice = rectifiedStrip.Slice((box.TopRow * stripW) + box.StartCol);
            ReadOnlyImageView2D<byte> charView = new(patchSlice, box.Width, box.Height, stripW);

            IndustrialOcrTrainer.ExtractNormalizedGlyphFeatures(
                charView,
                segmentParams.DarkOnLight,
                charGrid,
                charHProj,
                charVProj,
                charQuads,
                out float contrastRange);

            OcrMaxFieldingMatcher.MatchCharacter(
                fontModel,
                charGrid,
                charHProj,
                charVProj,
                charQuads,
                i,
                in fieldingRule,
                out char bestChar,
                out char runnerUpChar,
                out float bestScore,
                out float runnerUpScore,
                out float confusionMargin,
                out IndustrialOcrGlyphTemplate? matchedTemplate);

            OcvDefectFlags charFlags = OcvDefectFlags.None;
            if (bestScore < ocvParams.AcceptThreshold)
            {
                charFlags |= OcvDefectFlags.LowAcceptanceScore;
            }

            if (confusionMargin < ocvParams.ConfusionMarginThreshold)
            {
                charFlags |= OcvDefectFlags.ConfusableAmbiguity;
            }

            float ocvQuality = 0f;
            float worstQuadRetention = 0f;
            float shiftX = 0f;
            float shiftY = 0f;

            if (matchedTemplate is not null)
            {
                HalconProjectionOcvInspector.InspectCharacterQuality(
                    stripView,
                    in box,
                    matchedTemplate,
                    charHProj,
                    charVProj,
                    charQuads,
                    contrastRange,
                    in ocvParams,
                    out ocvQuality,
                    out worstQuadRetention,
                    out shiftX,
                    out shiftY,
                    out OcvDefectFlags ocvFlags);

                charFlags |= ocvFlags;
                if ((ocvFlags & OcvDefectFlags.MissingStrokeOrDot) != 0)
                {
                    // Missing stroke on digits like '8' also causes confusable ambiguity with '3'
                    charFlags |= OcvDefectFlags.ConfusableAmbiguity;
                }
            }
            else
            {
                charFlags |= OcvDefectFlags.FieldingSyntaxMismatch;
            }

            bool charPass = charFlags == OcvDefectFlags.None;
            lineFlags |= charFlags;

            sb.Append(bestChar);
            if (bestScore < minCharScore)
            {
                minCharScore = bestScore;
            }

            if (confusionMargin < minConfusionMargin)
            {
                minConfusionMargin = confusionMargin;
            }

            if (ocvQuality < minOcvQuality)
            {
                minOcvQuality = ocvQuality;
            }

            if (contrastRange < minContrast)
            {
                minContrast = contrastRange;
            }

            charResults[i] = new CharVerifyResult(
                i,
                bestChar,
                runnerUpChar,
                bestScore,
                runnerUpScore,
                confusionMargin,
                ocvQuality,
                contrastRange,
                worstQuadRetention,
                shiftX,
                shiftY,
                charFlags,
                charPass);
        }

        string decodedText = sb.ToString();
        if (ocvParams.VerifyCalendarDate && !OcrMaxFieldingMatcher.VerifyExpiryCalendarDate(decodedText))
        {
            lineFlags |= OcvDefectFlags.InvalidCalendarDate;
        }

        if (minContrast == float.MaxValue)
        {
            minContrast = 0f;
        }

        return new OcrOcvLineResult(
            decodedText,
            IsPass: lineFlags == OcvDefectFlags.None,
            lineFlags,
            minCharScore,
            minConfusionMargin,
            minOcvQuality,
            minContrast,
            charResults);
    }
}
