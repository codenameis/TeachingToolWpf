// File: src/VisionAlgorithm/OpenSrc/IndustrialOcrOcv/OcrMaxSegmenter.cs
using VisionAlgorithm.OpenSrc.BlobRle;
using VisionAlgorithm.OpenSrc.Common;

namespace VisionAlgorithm.OpenSrc.IndustrialOcrOcv;

/// <summary>
/// Cognex <c>OCRMax</c>-inspired character segmenter combining <see cref="SimdAffineWarper.WarpAffineBilinear"/>
/// fixture rectification, <see cref="RleMorphology.ClosingBox"/> dot-matrix bridging, and pitch-constrained
/// vertical projection valley splitting and fragment merging.
/// </summary>
public static class OcrMaxSegmenter
{
    private const int MaxSegmentedChars = 64;
    private const float SpaceGapPitchMultiplier = 1.55f;

    /// <summary>
    /// Rectifies the text-line region anchored at <paramref name="fixture"/> into <paramref name="rectifiedStripOut"/>
    /// and segments individual character bounding boxes into <paramref name="boxesOut"/>.
    /// </summary>
    public static int SegmentLine(
        ReadOnlyImageView2D<byte> image,
        in FixturePose2D fixture,
        in OcrMaxSegmentParams segmentParams,
        VisionWorkspace workspace,
        Span<byte> rectifiedStripOut,
        Span<SegmentedCharBox> boxesOut)
    {
        image.EnsureValid();
        fixture.EnsureValid();
        ArgumentNullException.ThrowIfNull(workspace);

        int w = segmentParams.RectifiedWidth;
        int h = segmentParams.RectifiedHeight;
        int pixelCount = checked(w * h);
        Span<float> warpScratch = workspace.GetFloatBufferA(pixelCount);

        SimdAffineWarper.WarpAffineBilinear(image, in fixture, w, h, rectifiedStripOut, warpScratch);

        if (segmentParams.PrintMethod == PrintMethodType.InkjetDotMatrix &&
            (segmentParams.DotClosingRadiusX > 0 || segmentParams.DotClosingRadiusY > 0))
        {
            BridgeInkjetDotMatrixStrip(
                rectifiedStripOut,
                w,
                h,
                segmentParams.DarkOnLight);
        }

        // Compute background median/mean and dynamic half-contrast threshold so anti-aliased fringes do not bloat bounding boxes
        double sum = 0.0;
        byte minPix = 255;
        byte maxPix = 0;
        for (int i = 0; i < pixelCount; i++)
        {
            byte p = rectifiedStripOut[i];
            sum += p;
            if (p < minPix) minPix = p;
            if (p > maxPix) maxPix = p;
        }

        float mean = (float)(sum / pixelCount);
        float dynamicContrast = segmentParams.DarkOnLight ? (mean - minPix) : (maxPix - mean);
        Span<byte> binaryMask = workspace.GetByteBufferB(pixelCount, clear: true);
        float offset = MathF.Max(segmentParams.ForegroundContrastOffset, dynamicContrast * 0.42f);

        for (int i = 0; i < pixelCount; i++)
        {
            byte p = rectifiedStripOut[i];
            bool isFg = segmentParams.DarkOnLight
                ? (mean - p) >= offset
                : (p - mean) >= offset;
            binaryMask[i] = isFg ? byte.MaxValue : (byte)0;
        }

        // Apply RLE morphological closing when InkjetDotMatrix is active to bridge discrete 5x7 / 7x9 dots
        RleRegion fgRegion = RleRegion.Threshold(binaryMask, w, h, w, minIntensity: 128);
        if (segmentParams.PrintMethod == PrintMethodType.InkjetDotMatrix &&
            (segmentParams.DotClosingRadiusX > 0 || segmentParams.DotClosingRadiusY > 0))
        {
            fgRegion = RleMorphology.ClosingBox(fgRegion, segmentParams.DotClosingRadiusX, segmentParams.DotClosingRadiusY);
        }

        // Accumulate vertical and horizontal foreground projections from RLE chords
        Span<int> colProjection = stackalloc int[w];
        Span<int> rowProjection = stackalloc int[h];
        colProjection.Clear();
        rowProjection.Clear();

        IReadOnlyList<PixelRun> runs = fgRegion.Runs;
        for (int i = 0; i < runs.Count; i++)
        {
            PixelRun run = runs[i];
            rowProjection[run.Row] += run.Length;
            for (int c = run.StartCol; c <= run.EndCol; c++)
            {
                colProjection[c]++;
            }
        }

        // Determine tight vertical row bounds of the text line
        int topRow = 0;
        int bottomRow = h - 1;
        while (topRow < h && rowProjection[topRow] <= 1)
        {
            topRow++;
        }

        while (bottomRow > topRow && rowProjection[bottomRow] <= 1)
        {
            bottomRow--;
        }

        if (topRow >= bottomRow)
        {
            return 0;
        }

        int lineHeight = (bottomRow - topRow) + 1;
        const int minColHits = 1;

        // Also compute raw unbridged column projection for accurate valley splitting when adjacent characters touch
        Span<int> rawColProjection = stackalloc int[w];
        rawColProjection.Clear();
        for (int r = topRow; r <= bottomRow; r++)
        {
            int rowOffset = r * w;
            for (int c = 0; c < w; c++)
            {
                if (binaryMask[rowOffset + c] != 0)
                {
                    rawColProjection[c]++;
                }
            }
        }

        // Stage 1: Initial interval extraction over bridged column projection
        Span<int> rawStarts = stackalloc int[MaxSegmentedChars];
        Span<int> rawEnds = stackalloc int[MaxSegmentedChars];
        int rawCount = 0;

        int col = 0;
        while (col < w && rawCount < MaxSegmentedChars)
        {
            while (col < w && colProjection[col] < minColHits)
            {
                col++;
            }

            if (col >= w)
            {
                break;
            }

            int start = col;
            while (col < w && colProjection[col] >= minColHits)
            {
                col++;
            }

            int end = col - 1;
            rawStarts[rawCount] = start;
            rawEnds[rawCount] = end;
            rawCount++;
        }

        // Stage 2: Merge narrow dot fragments (< MinCharWidth) into adjacent character intervals if within pitch
        Span<int> mergedStarts = stackalloc int[MaxSegmentedChars];
        Span<int> mergedEnds = stackalloc int[MaxSegmentedChars];
        int mergedCount = 0;

        for (int i = 0; i < rawCount; i++)
        {
            int s = rawStarts[i];
            int e = rawEnds[i];
            int width = (e - s) + 1;

            if (mergedCount > 0)
            {
                int prevS = mergedStarts[mergedCount - 1];
                int prevE = mergedEnds[mergedCount - 1];
                int gap = s - prevE - 1;
                int combinedW = (e - prevS) + 1;
                if (gap <= 2 && combinedW <= segmentParams.MaxCharWidth &&
                    (width < segmentParams.MinCharWidth || (prevE - prevS + 1) < segmentParams.MinCharWidth))
                {
                    mergedEnds[mergedCount - 1] = e;
                    continue;
                }
            }

            mergedStarts[mergedCount] = s;
            mergedEnds[mergedCount] = e;
            mergedCount++;
        }

        // Stage 3: Pitch-guided Valley Split for wide touching/smeared boxes (> MaxCharWidth)
        Span<int> finalStarts = stackalloc int[MaxSegmentedChars];
        Span<int> finalEnds = stackalloc int[MaxSegmentedChars];
        Span<float> bridgeDensities = stackalloc float[MaxSegmentedChars];
        int finalCount = 0;

        for (int i = 0; i < mergedCount && finalCount < MaxSegmentedChars; i++)
        {
            int s = mergedStarts[i];
            int e = mergedEnds[i];
            SplitOverwideSegment(
                s,
                e,
                lineHeight,
                in segmentParams,
                rawColProjection,
                finalStarts,
                finalEnds,
                bridgeDensities,
                ref finalCount);
        }

        // Stage 4: Compute tight per-character vertical bounds and inter-word space flags
        int outCount = Math.Min(finalCount, boxesOut.Length);
        float spaceGapThreshold = segmentParams.ExpectedPitch * 0.92f;

        for (int i = 0; i < outCount; i++)
        {
            int cs = finalStarts[i];
            int ce = finalEnds[i];
            int charTop = topRow;
            int charBottom = bottomRow;

            // Refine top/bottom rows for this character column slice
            for (int r = topRow; r <= bottomRow; r++)
            {
                bool hasFg = false;
                int rowOff = r * w;
                for (int c = cs; c <= ce; c++)
                {
                    if (binaryMask[rowOff + c] != 0)
                    {
                        hasFg = true;
                        break;
                    }
                }

                if (hasFg)
                {
                    charTop = r;
                    break;
                }
            }

            for (int r = bottomRow; r >= charTop; r--)
            {
                bool hasFg = false;
                int rowOff = r * w;
                for (int c = cs; c <= ce; c++)
                {
                    if (binaryMask[rowOff + c] != 0)
                    {
                        hasFg = true;
                        break;
                    }
                }

                if (hasFg)
                {
                    charBottom = r;
                    break;
                }
            }

            bool precededBySpace = false;
            if (i > 0)
            {
                int gap = cs - finalEnds[i - 1] - 1;
                int centerDist = ((cs + ce) / 2) - ((finalStarts[i - 1] + finalEnds[i - 1]) / 2);
                precededBySpace = gap >= spaceGapThreshold && centerDist >= (segmentParams.ExpectedPitch * SpaceGapPitchMultiplier);
            }

            boxesOut[i] = new SegmentedCharBox(
                i,
                cs,
                ce,
                charTop,
                charBottom,
                precededBySpace,
                bridgeDensities[i]);
        }

        return outCount;
    }

    private static void BridgeInkjetDotMatrixStrip(
        Span<byte> strip,
        int width,
        int height,
        bool darkOnLight)
    {
        int len = checked(width * height);
        byte[] srcBuf = new byte[len];
        strip.Slice(0, len).CopyTo(srcBuf);
        ReadOnlySpan<byte> src = srcBuf;

        // Restore discrete 5x7 / 7x9 dot radius (+0.55 px isotropic soft dilation) and bridge 1..2 px horizontal/vertical inter-dot gaps
        for (int y = 2; y < height - 2; y++)
        {
            int ym1 = (y - 1) * width;
            int yc = y * width;
            int yp1 = (y + 1) * width;
            int ym2 = (y - 2) * width;
            int yp2 = (y + 2) * width;
            int ym3 = Math.Max(0, y - 3) * width;
            int yp3 = Math.Min(height - 1, y + 3) * width;

            for (int x = 2; x < width - 2; x++)
            {
                int c = src[yc + x];
                int l1 = src[yc + x - 1];
                int r1 = src[yc + x + 1];
                int u1 = src[ym1 + x];
                int d1 = src[yp1 + x];

                int ul = src[ym1 + x - 1];
                int ur = src[ym1 + x + 1];
                int dl = src[yp1 + x - 1];
                int dr = src[yp1 + x + 1];

                if (darkOnLight)
                {
                    int n4 = Math.Min(c, Math.Min(Math.Min(l1, r1), Math.Min(u1, d1)));
                    int n8 = Math.Min(n4, Math.Min(Math.Min(ul, ur), Math.Min(dl, dr)));
                    int expandedDot = ((c * 3) + (n4 * 9) + (n8 * 4) + 8) >> 4;

                    int hBridge = Math.Max(Math.Min(l1, src[yc + x - 2]), Math.Min(r1, src[yc + x + 2]));
                    int vBridge = Math.Max(Math.Min(u1, src[ym2 + x]), Math.Min(d1, src[yp2 + x]));
                    int vDropoutBridge = Math.Max(src[ym3 + x], src[yp3 + x]);
                    int dBridgeA = Math.Max(ul, dr);
                    int dBridgeB = Math.Max(ur, dl);

                    int bridged = Math.Min(expandedDot, Math.Min(Math.Min(hBridge, vBridge), Math.Min(vDropoutBridge, Math.Min(dBridgeA, dBridgeB))));
                    strip[yc + x] = (byte)Math.Clamp(bridged, 0, 255);
                }
                else
                {
                    int n4 = Math.Max(c, Math.Max(Math.Max(l1, r1), Math.Max(u1, d1)));
                    int n8 = Math.Max(n4, Math.Max(Math.Max(ul, ur), Math.Max(dl, dr)));
                    int expandedDot = ((c * 3) + (n4 * 9) + (n8 * 4) + 8) >> 4;

                    int hBridge = Math.Min(Math.Max(l1, src[yc + x - 2]), Math.Max(r1, src[yc + x + 2]));
                    int vBridge = Math.Min(Math.Max(u1, src[ym2 + x]), Math.Max(d1, src[yp2 + x]));
                    int vDropoutBridge = Math.Min(src[ym3 + x], src[yp3 + x]);
                    int dBridgeA = Math.Min(ul, dr);
                    int dBridgeB = Math.Min(ur, dl);

                    int bridged = Math.Max(expandedDot, Math.Max(Math.Max(hBridge, vBridge), Math.Max(vDropoutBridge, Math.Max(dBridgeA, dBridgeB))));
                    strip[yc + x] = (byte)Math.Clamp(bridged, 0, 255);
                }
            }
        }

        // Second pass: widen newly bridged 1px filaments so bridged stroke width matches continuous stroke thickness
        strip.Slice(0, len).CopyTo(srcBuf);
        for (int y = 1; y < height - 1; y++)
        {
            int ym1 = (y - 1) * width;
            int yc = y * width;
            int yp1 = (y + 1) * width;
            for (int x = 1; x < width - 1; x++)
            {
                int c = src[yc + x];
                int l1 = src[yc + x - 1];
                int r1 = src[yc + x + 1];
                int u1 = src[ym1 + x];
                int d1 = src[yp1 + x];
                if (darkOnLight)
                {
                    int n4 = Math.Min(c, Math.Min(Math.Min(l1, r1), Math.Min(u1, d1)));
                    strip[yc + x] = (byte)(((c * 9) + (n4 * 7) + 8) >> 4);
                }
                else
                {
                    int n4 = Math.Max(c, Math.Max(Math.Max(l1, r1), Math.Max(u1, d1)));
                    strip[yc + x] = (byte)(((c * 9) + (n4 * 7) + 8) >> 4);
                }
            }
        }
    }

    private static void SplitOverwideSegment(
        int startCol,
        int endCol,
        int lineHeight,
        in OcrMaxSegmentParams segmentParams,
        ReadOnlySpan<int> rawColProjection,
        Span<int> finalStarts,
        Span<int> finalEnds,
        Span<float> bridgeDensities,
        ref int finalCount)
    {
        int curStart = startCol;
        while (((endCol - curStart) + 1) > segmentParams.MaxCharWidth && finalCount < finalStarts.Length - 1)
        {
            int expectedSplit = curStart + segmentParams.ExpectedPitch - 1;
            int searchRadius = Math.Max(3, segmentParams.ExpectedPitch / 4);
            int searchMin = Math.Max(curStart + segmentParams.MinCharWidth, expectedSplit - searchRadius);
            int searchMax = Math.Min(endCol - segmentParams.MinCharWidth, expectedSplit + searchRadius);

            int bestCol = Math.Clamp(expectedSplit, curStart + segmentParams.MinCharWidth, endCol - segmentParams.MinCharWidth);
            int minProj = int.MaxValue;
            for (int c = searchMin; c <= searchMax; c++)
            {
                if (rawColProjection[c] < minProj)
                {
                    minProj = rawColProjection[c];
                    bestCol = c;
                }
            }

            float bridgeRatio = minProj < int.MaxValue
                ? (float)minProj / Math.Max(1, lineHeight)
                : 0f;

            finalStarts[finalCount] = curStart;
            finalEnds[finalCount] = bestCol;
            bridgeDensities[finalCount] = bridgeRatio;
            finalCount++;

            curStart = bestCol + 1;
        }

        if (finalCount < finalStarts.Length && curStart <= endCol)
        {
            finalStarts[finalCount] = curStart;
            finalEnds[finalCount] = endCol;
            bridgeDensities[finalCount] = 0f;
            finalCount++;
        }
    }
}
