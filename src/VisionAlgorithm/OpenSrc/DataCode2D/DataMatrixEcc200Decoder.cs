// File: src/VisionAlgorithm/OpenSrc/DataCode2D/DataMatrixEcc200Decoder.cs
using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using VisionAlgorithm.OpenSrc.Common;

namespace VisionAlgorithm.OpenSrc.DataCode2D;

/// <summary>
/// First-principles ISO/IEC 16022 ECC 200 DataMatrix 2D Symbol Encoder/Decoder and ISO/IEC 15415 Print Quality Verifier.
/// Implements L-finder &amp; alternating timing border localization, subpixel perspective grid sampling,
/// Utah L-shaped 8-bit module placement, $\text{GF}(2^8)$ with primitive polynomial $x^8 + x^5 + x^3 + x^2 + 1 = 301$
/// Reed-Solomon syndrome verification and single-symbol error correction, and ASCII codeword decoding.
/// </summary>
public static class DataMatrixEcc200Decoder
{
    public const int DefaultMaxSymbols = 32;

    // [Math Why]: ISO/IEC 16022 ECC 200 Galois Field GF(2^8) primitive polynomial p(x) = x^8 + x^5 + x^3 + x^2 + 1 = 0x12D (301).
    private const int GfPrimitivePolynomial = 301;
    private const int GfFieldSize = 256;
    private const int GfMaxElementIndex = 255;
    private const byte AsciiPadCodeword = 129;
    private const int BitsPerCodeword = 8;
    private const int MaxCandidateSubBoxes = 64;
    private const int ComponentCellShift = 2;
    private const int MinContrastGrayRange = 25;
    private const int MinSymbolBoxSpanPx = 10;
    private const float HalfModuleOffset = 0.5f;
    private const float MaxByteNormalizedScale = 1.0f / 255.0f;
    private const float DuplicateCenterDistanceRatio = 0.45f;
    private const float ReadingOrderRowBandRatio = 0.55f;
    private const float MinCandidateAspectRatio = 0.65f;
    private const float MaxCandidateAspectRatio = 1.55f;
    private const float IsoGradeAThreshold = 0.70f;
    private const float IsoGradeBThreshold = 0.55f;
    private const float IsoGradeCThreshold = 0.40f;
    private const float IsoGradeDThreshold = 0.25f;

    private static readonly byte[] GfExpTable = new byte[GfFieldSize * 2];
    private static readonly byte[] GfLogTable = new byte[GfFieldSize];

    internal static readonly SymbolGeometrySpec[] SupportedSymbolGeometries =
    [
        new(SymbolRows: 10, SymbolCols: 10, DataCodewords: 3, EccCodewords: 5),
        new(SymbolRows: 12, SymbolCols: 12, DataCodewords: 5, EccCodewords: 7),
        new(SymbolRows: 14, SymbolCols: 14, DataCodewords: 8, EccCodewords: 10),
        new(SymbolRows: 16, SymbolCols: 16, DataCodewords: 12, EccCodewords: 12),
        new(SymbolRows: 18, SymbolCols: 18, DataCodewords: 18, EccCodewords: 14),
        new(SymbolRows: 20, SymbolCols: 20, DataCodewords: 22, EccCodewords: 18)
    ];

    static DataMatrixEcc200Decoder()
    {
        int value = 1;
        for (int i = 0; i < GfMaxElementIndex; i++)
        {
            GfExpTable[i] = (byte)value;
            GfExpTable[i + GfMaxElementIndex] = (byte)value;
            GfLogTable[value] = (byte)i;

            value <<= 1;
            if (value >= GfFieldSize)
            {
                value ^= GfPrimitivePolynomial;
            }
        }

        GfLogTable[0] = 0;
    }

    /// <summary>
    /// Locates, samples, error-corrects, and decodes an ISO/IEC 16022 ECC 200 DataMatrix symbol in <paramref name="imageView"/>,
    /// returning its payload, exact world center &amp; dimensions, and ISO/IEC 15415 print quality verification metrics.
    /// Supports both tight single-symbol ROIs and wide/multi-object ROIs containing multiple 2D symbols, 1D barcodes, or text.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static DataMatrixDecodeResult Decode(ReadOnlyImageView2D<byte> imageView)
    {
        imageView.EnsureValid();

        if (!TryLocateSymbolBoundingBox(
            imageView,
            out float minX,
            out float minY,
            out float maxX,
            out float maxY,
            out byte darkRef,
            out byte lightRef,
            out bool isDarkOnLight))
        {
            return default;
        }

        // Stage 1: Try decoding the primary foreground bounding box directly (with 0..2px edge trim/pad refinement)
        if (TryDecodeCandidateBoxWithRefinement(
            imageView,
            minX,
            minY,
            maxX,
            maxY,
            darkRef,
            lightRef,
            isDarkOnLight,
            out DataMatrixDecodeResult directResult) &&
            directResult.CorrectedByteErrors == 0 &&
            directResult.FixedPatternDamageScore >= 0.85f)
        {
            return directResult;
        }

        DataMatrixDecodeResult bestResult = directResult;

        // Stage 2: When the ROI spans multiple symbols (e.g., two DataMatrix codes on the same canvas) or surrounding text/borders,
        // decompose the foreground into candidate 2D square/compact sub-regions and evaluate each candidate with local contrast.
        Span<(int X0, int Y0, int X1, int Y1)> candidateBoxes = stackalloc (int X0, int Y0, int X1, int Y1)[MaxCandidateSubBoxes];
        int candidateCount = ExtractCandidate2DSymbolBoxes(
            imageView,
            (int)MathF.Floor(minX),
            (int)MathF.Floor(minY),
            (int)MathF.Ceiling(maxX) - 1,
            (int)MathF.Ceiling(maxY) - 1,
            darkRef,
            lightRef,
            isDarkOnLight,
            candidateBoxes);

        float roiCenterX = imageView.Width * HalfModuleOffset;
        float roiCenterY = imageView.Height * HalfModuleOffset;

        for (int i = 0; i < candidateCount; i++)
        {
            var box = candidateBoxes[i];
            ComputeSubBoxDarkLightRange(
                imageView,
                box.X0,
                box.Y0,
                box.X1,
                box.Y1,
                out byte subDark,
                out byte subLight);
            if (subLight - subDark < MinContrastGrayRange)
            {
                subDark = darkRef;
                subLight = lightRef;
            }

            if (TryDecodeCandidateBoxWithRefinement(
                imageView,
                box.X0,
                box.Y0,
                box.X1 + 1.0f,
                box.Y1 + 1.0f,
                subDark,
                subLight,
                isDarkOnLight,
                out DataMatrixDecodeResult subResult))
            {
                if (!bestResult.IsDecoded || IsBetterDecodeCandidate(subResult, bestResult, roiCenterX, roiCenterY))
                {
                    bestResult = subResult;
                }
            }
        }

        return bestResult;
    }

    /// <summary>
    /// Locates, error-corrects, and decodes all distinct ISO/IEC 16022 ECC 200 DataMatrix symbols inside <paramref name="imageView"/>
    /// across both Dark-on-Light and Light-on-Dark polarities, sorted in reading order (top-to-bottom row band, left-to-right).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static DataMatrixDecodeResult[] DecodeAll(ReadOnlyImageView2D<byte> imageView, int maxSymbols = DefaultMaxSymbols)
    {
        imageView.EnsureValid();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSymbols);

        if (!TryLocateSymbolBoundingBox(
            imageView,
            out _,
            out _,
            out _,
            out _,
            out byte darkRef,
            out byte lightRef,
            out bool primaryDarkOnLight))
        {
            return [];
        }

        var accepted = new List<DataMatrixDecodeResult>(Math.Min(maxSymbols, 8));
        CollectDataMatrixSymbolsWithPolarity(imageView, darkRef, lightRef, primaryDarkOnLight, maxSymbols, accepted);

        if (accepted.Count < maxSymbols)
        {
            CollectDataMatrixSymbolsWithPolarity(imageView, darkRef, lightRef, !primaryDarkOnLight, maxSymbols, accepted);
        }

        if (accepted.Count == 0)
        {
            return [];
        }

        SortDataMatrixResultsInReadingOrder(accepted);
        return accepted.ToArray();
    }

    /// <summary>
    /// Decodes all DataMatrix ECC 200 symbols in <paramref name="imageView"/> and returns a standardized
    /// <see cref="VisionMultiResult{TItem}"/> supporting lossless multi-envelope SPC &amp; <c>VisionCanvas</c> replay export.
    /// </summary>
    public static VisionMultiResult<DataMatrixDecodeResult> DecodeMulti(ReadOnlyImageView2D<byte> imageView, int maxSymbols = DefaultMaxSymbols)
    {
        DataMatrixDecodeResult[] items = DecodeAll(imageView, maxSymbols);
        return VisionMultiResult<DataMatrixDecodeResult>.Create(VisionToolCategory.OcrVerification, items);
    }

    private static void CollectDataMatrixSymbolsWithPolarity(
        ReadOnlyImageView2D<byte> imageView,
        byte darkRef,
        byte lightRef,
        bool isDarkOnLight,
        int maxSymbols,
        List<DataMatrixDecodeResult> accepted)
    {
        float midThreshold = (lightRef + darkRef) * HalfModuleOffset;
        if (!TightenSubBoxForeground(
            imageView,
            0,
            0,
            imageView.Width - 1,
            imageView.Height - 1,
            midThreshold,
            isDarkOnLight,
            out int boundX0,
            out int boundY0,
            out int boundX1,
            out int boundY1))
        {
            return;
        }

        Span<(int X0, int Y0, int X1, int Y1)> candidateBoxes = stackalloc (int X0, int Y0, int X1, int Y1)[MaxCandidateSubBoxes];
        int candidateCount = ExtractCandidate2DSymbolBoxes(
            imageView,
            boundX0,
            boundY0,
            boundX1,
            boundY1,
            darkRef,
            lightRef,
            isDarkOnLight,
            candidateBoxes);

        // Also include the tight overall foreground box so single-symbol tight ROIs always remain covered.
        AddUniqueCandidateBox(candidateBoxes, ref candidateCount, boundX0, boundY0, boundX1, boundY1);

        for (int i = 0; i < candidateCount && accepted.Count < maxSymbols; i++)
        {
            var box = candidateBoxes[i];
            ComputeSubBoxDarkLightRange(
                imageView,
                box.X0,
                box.Y0,
                box.X1,
                box.Y1,
                out byte subDark,
                out byte subLight);
            if (subLight - subDark < MinContrastGrayRange)
            {
                subDark = darkRef;
                subLight = lightRef;
            }

            if (TryDecodeCandidateBoxWithRefinement(
                imageView,
                box.X0,
                box.Y0,
                box.X1 + 1.0f,
                box.Y1 + 1.0f,
                subDark,
                subLight,
                isDarkOnLight,
                out DataMatrixDecodeResult decoded))
            {
                MergeOrAppendDataMatrixSymbol(accepted, in decoded, maxSymbols);
            }
        }
    }

    private static void MergeOrAppendDataMatrixSymbol(
        List<DataMatrixDecodeResult> accepted,
        in DataMatrixDecodeResult candidate,
        int maxSymbols)
    {
        float candSpan = MathF.Min(candidate.WidthPixels, candidate.HeightPixels);
        for (int i = 0; i < accepted.Count; i++)
        {
            DataMatrixDecodeResult existing = accepted[i];
            float minSpan = MathF.Max(MinSymbolBoxSpanPx, MathF.Min(candSpan, MathF.Min(existing.WidthPixels, existing.HeightPixels)));
            if (Vector2.Distance(candidate.CenterWorld, existing.CenterWorld) <= (minSpan * DuplicateCenterDistanceRatio))
            {
                if (IsHigherQualityDataMatrix(in candidate, in existing))
                {
                    accepted[i] = candidate;
                }

                return;
            }
        }

        if (accepted.Count < maxSymbols)
        {
            accepted.Add(candidate);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsHigherQualityDataMatrix(in DataMatrixDecodeResult candidate, in DataMatrixDecodeResult existing)
    {
        if (candidate.CorrectedByteErrors != existing.CorrectedByteErrors)
        {
            return candidate.CorrectedByteErrors < existing.CorrectedByteErrors;
        }

        if (MathF.Abs(candidate.FixedPatternDamageScore - existing.FixedPatternDamageScore) > 0.02f)
        {
            return candidate.FixedPatternDamageScore > existing.FixedPatternDamageScore;
        }

        return candidate.ModulationScore > existing.ModulationScore;
    }

    private static void SortDataMatrixResultsInReadingOrder(List<DataMatrixDecodeResult> results)
    {
        if (results.Count <= 1)
        {
            return;
        }

        results.Sort(static (a, b) =>
        {
            float rowBand = MathF.Max(MinSymbolBoxSpanPx, MathF.Min(a.HeightPixels, b.HeightPixels) * ReadingOrderRowBandRatio);
            float dy = a.CenterWorld.Y - b.CenterWorld.Y;
            if (MathF.Abs(dy) > rowBand)
            {
                return dy < 0.0f ? -1 : 1;
            }

            return a.CenterWorld.X.CompareTo(b.CenterWorld.X);
        });
    }

    private static bool IsBetterDecodeCandidate(
        in DataMatrixDecodeResult candidate,
        in DataMatrixDecodeResult currentBest,
        float roiCenterX,
        float roiCenterY)
    {
        if (!currentBest.IsDecoded)
        {
            return true;
        }

        if (candidate.CorrectedByteErrors != currentBest.CorrectedByteErrors)
        {
            return candidate.CorrectedByteErrors < currentBest.CorrectedByteErrors;
        }

        float scoreDiff = candidate.FixedPatternDamageScore - currentBest.FixedPatternDamageScore;
        if (MathF.Abs(scoreDiff) > 0.04f)
        {
            return scoreDiff > 0.0f;
        }

        float candDistSq = Vector2.DistanceSquared(candidate.CenterWorld, new Vector2(roiCenterX, roiCenterY));
        float bestDistSq = Vector2.DistanceSquared(currentBest.CenterWorld, new Vector2(roiCenterX, roiCenterY));
        return candDistSq < bestDistSq;
    }

    private static bool TryDecodeCandidateBoxWithRefinement(
        ReadOnlyImageView2D<byte> imageView,
        float minX,
        float minY,
        float maxX,
        float maxY,
        byte darkRef,
        byte lightRef,
        bool isDarkOnLight,
        out DataMatrixDecodeResult bestResult)
    {
        bestResult = default;
        float baseW = maxX - minX;
        float baseH = maxY - minY;
        if (baseW < 8.0f || baseH < 8.0f)
        {
            return false;
        }

        // Fast path: test axis-aligned bounds (0°, 90°, 180°, -90°) with small trim adjustments
        ReadOnlySpan<(float dL, float dT, float dR, float dB)> offsets =
        [
            (0f, 0f, 0f, 0f),
            (1f, 1f, -1f, -1f),
            (-1f, -1f, 1f, 1f),
            (2f, 2f, -2f, -2f),
            (1f, 0f, 0f, -1f),
            (0f, 1f, -1f, 0f)
        ];

        ReadOnlySpan<float> cardinalAngles = [0.0f, MathF.PI * 0.5f, MathF.PI, -MathF.PI * 0.5f];
        float symbolContrast = Math.Abs(lightRef - darkRef) * MaxByteNormalizedScale;
        float midThreshold = (lightRef + darkRef) * HalfModuleOffset;

        for (int q = 0; q < cardinalAngles.Length; q++)
        {
            float baseAngle = cardinalAngles[q];
            bool swapDims = (q & 1) != 0;

            for (int o = 0; o < offsets.Length; o++)
            {
                var (dL, dT, dR, dB) = offsets[o];
                float candMinX = Math.Clamp(minX + dL, 0.0f, imageView.Width - 2.0f);
                float candMinY = Math.Clamp(minY + dT, 0.0f, imageView.Height - 2.0f);
                float candMaxX = Math.Clamp(maxX + dR, candMinX + 8.0f, (float)imageView.Width);
                float candMaxY = Math.Clamp(maxY + dB, candMinY + 8.0f, (float)imageView.Height);
                float rawW = candMaxX - candMinX;
                float rawH = candMaxY - candMinY;
                float boxWidth = swapDims ? rawH : rawW;
                float boxHeight = swapDims ? rawW : rawH;
                if (boxWidth < 8.0f || boxHeight < 8.0f)
                {
                    continue;
                }

                float axialNonUniformity = MathF.Abs(boxWidth - boxHeight) / MathF.Max(boxWidth, boxHeight);
                if (axialNonUniformity > 0.35f)
                {
                    continue;
                }

                Vector2 centerWorld = new((candMinX + candMaxX) * HalfModuleOffset, (candMinY + candMaxY) * HalfModuleOffset);

                for (int g = 0; g < SupportedSymbolGeometries.Length; g++)
                {
                    SymbolGeometrySpec geom = SupportedSymbolGeometries[g];
                    if (TryDecodeWithGeometry(
                        imageView,
                        geom,
                        centerWorld,
                        boxWidth,
                        boxHeight,
                        baseAngle,
                        midThreshold,
                        darkRef,
                        lightRef,
                        isDarkOnLight,
                        symbolContrast,
                        axialNonUniformity,
                        out DataMatrixDecodeResult result))
                    {
                        if (result.CorrectedByteErrors == 0 && result.FixedPatternDamageScore >= 0.90f)
                        {
                            bestResult = result;
                            return true;
                        }

                        if (!bestResult.IsDecoded ||
                            result.CorrectedByteErrors < bestResult.CorrectedByteErrors ||
                            (result.CorrectedByteErrors == bestResult.CorrectedByteErrors &&
                             result.FixedPatternDamageScore > bestResult.FixedPatternDamageScore))
                        {
                            bestResult = result;
                        }
                    }
                }

                if (bestResult.IsDecoded && bestResult.CorrectedByteErrors == 0 && bestResult.FixedPatternDamageScore >= 0.85f)
                {
                    return true;
                }
            }
        }

        // General 360-degree path: compute Minimum-Area Oriented Bounding Box (MABB) over foreground boundary extrema
        // and evaluate all 4 quadrant orientations (phi + k * 90 deg) with small scale/trim adjustments.
        if (EstimateMinimumAreaOrientedBox(
            imageView,
            (int)MathF.Floor(minX),
            (int)MathF.Floor(minY),
            (int)MathF.Ceiling(maxX) - 1,
            (int)MathF.Ceiling(maxY) - 1,
            midThreshold,
            isDarkOnLight,
            out Vector2 obbCenter,
            out float obbSpanU,
            out float obbSpanV,
            out float obbAnglePhi))
        {
            ReadOnlySpan<float> trimDeltas = [0.0f, -1.0f, 1.0f, -2.0f, 2.0f];
            ReadOnlySpan<float> fineAngleNudges = [0.0f, -0.020f, 0.020f];

            for (int q = 0; q < 4; q++)
            {
                float quadBaseAngle = NormalizeRadians(obbAnglePhi + (q * MathF.PI * 0.5f));
                float quadBaseW = (q & 1) == 0 ? obbSpanU : obbSpanV;
                float quadBaseH = (q & 1) == 0 ? obbSpanV : obbSpanU;

                for (int a = 0; a < fineAngleNudges.Length; a++)
                {
                    float testAngle = NormalizeRadians(quadBaseAngle + fineAngleNudges[a]);
                    for (int t = 0; t < trimDeltas.Length; t++)
                    {
                        float boxWidth = quadBaseW + trimDeltas[t];
                        float boxHeight = quadBaseH + trimDeltas[t];
                        if (boxWidth < 8.0f || boxHeight < 8.0f)
                        {
                            continue;
                        }

                        float axialNonUniformity = MathF.Abs(boxWidth - boxHeight) / MathF.Max(boxWidth, boxHeight);
                        if (axialNonUniformity > 0.35f)
                        {
                            continue;
                        }

                        for (int g = 0; g < SupportedSymbolGeometries.Length; g++)
                        {
                            SymbolGeometrySpec geom = SupportedSymbolGeometries[g];
                            if (TryDecodeWithGeometry(
                                imageView,
                                geom,
                                obbCenter,
                                boxWidth,
                                boxHeight,
                                testAngle,
                                midThreshold,
                                darkRef,
                                lightRef,
                                isDarkOnLight,
                                symbolContrast,
                                axialNonUniformity,
                                out DataMatrixDecodeResult rotResult))
                            {
                                if (rotResult.CorrectedByteErrors == 0 && rotResult.FixedPatternDamageScore >= 0.90f)
                                {
                                    bestResult = rotResult;
                                    return true;
                                }

                                if (!bestResult.IsDecoded ||
                                    rotResult.CorrectedByteErrors < bestResult.CorrectedByteErrors ||
                                    (rotResult.CorrectedByteErrors == bestResult.CorrectedByteErrors &&
                                     rotResult.FixedPatternDamageScore > bestResult.FixedPatternDamageScore))
                                {
                                    bestResult = rotResult;
                                }
                            }
                        }
                    }

                    if (bestResult.IsDecoded && bestResult.CorrectedByteErrors == 0 && bestResult.FixedPatternDamageScore >= 0.85f)
                    {
                        return true;
                    }
                }
            }
        }

        return bestResult.IsDecoded;
    }

    private static void ComputeSubBoxDarkLightRange(
        ReadOnlyImageView2D<byte> imageView,
        int x0,
        int y0,
        int x1,
        int y1,
        out byte darkRef,
        out byte lightRef)
    {
        darkRef = byte.MaxValue;
        lightRef = byte.MinValue;
        int cx0 = Math.Clamp(x0 - 2, 0, imageView.Width - 1);
        int cy0 = Math.Clamp(y0 - 2, 0, imageView.Height - 1);
        int cx1 = Math.Clamp(x1 + 2, cx0, imageView.Width - 1);
        int cy1 = Math.Clamp(y1 + 2, cy0, imageView.Height - 1);

        for (int y = cy0; y <= cy1; y++)
        {
            ReadOnlySpan<byte> row = imageView.GetRowSpan(y);
            for (int x = cx0; x <= cx1; x++)
            {
                byte v = row[x];
                if (v < darkRef) darkRef = v;
                if (v > lightRef) lightRef = v;
            }
        }
    }

    private static int ExtractCandidate2DSymbolBoxes(
        ReadOnlyImageView2D<byte> imageView,
        int boundX0,
        int boundY0,
        int boundX1,
        int boundY1,
        byte darkRef,
        byte lightRef,
        bool isDarkOnLight,
        Span<(int X0, int Y0, int X1, int Y1)> candidates)
    {
        int width = imageView.Width;
        int height = imageView.Height;
        boundX0 = Math.Clamp(boundX0, 0, width - 1);
        boundY0 = Math.Clamp(boundY0, 0, height - 1);
        boundX1 = Math.Clamp(boundX1, boundX0, width - 1);
        boundY1 = Math.Clamp(boundY1, boundY0, height - 1);

        float midThreshold = (lightRef + darkRef) * HalfModuleOffset;
        int count = 0;

        // 0. 2D 8-connected coarse cell component clustering (4x4 px cells):
        // Separates diagonally or arbitrarily arranged multiple 2D symbols whose X and Y projections overlap simultaneously,
        // while naturally bridging 1..2 module gaps along alternating timing borders.
        ExtractConnectedCellCandidateBoxes(
            imageView,
            boundX0,
            boundY0,
            boundX1,
            boundY1,
            midThreshold,
            isDarkOnLight,
            candidates,
            ref count);

        // 1. Horizontal & vertical projection band splitting:
        // Separates side-by-side symbols (like the two DataMatrix symbols in the sample scene or 1D barcode + 2D code in OCR7 Section E)
        // and separates stacked text banners from the 2D matrix block.
        int[] rowFgPooled = ArrayPool<int>.Shared.Rent(height);
        int[] colFgPooled = ArrayPool<int>.Shared.Rent(width);
        try
        {
            Span<int> rowFg = rowFgPooled.AsSpan(0, height);
            Span<int> colFg = colFgPooled.AsSpan(0, width);
            rowFg.Slice(boundY0, boundY1 - boundY0 + 1).Clear();

            for (int y = boundY0; y <= boundY1; y++)
            {
                ReadOnlySpan<byte> row = imageView.GetRowSpan(y);
                int hits = 0;
                for (int x = boundX0; x <= boundX1; x++)
                {
                    bool fg = isDarkOnLight ? row[x] < midThreshold : row[x] > midThreshold;
                    if (fg)
                    {
                        hits++;
                    }
                }

                rowFg[y] = hits;
            }

            Span<(int Y0, int Y1)> rowBands = stackalloc (int Y0, int Y1)[24];
            int rowBandCount = SplitProjectionIntervals(rowFg, boundY0, boundY1, minGapRun: 3, minBandSpan: MinSymbolBoxSpanPx, rowBands);
            if (rowBandCount == 0 && (boundY1 - boundY0 + 1) >= MinSymbolBoxSpanPx)
            {
                rowBands[0] = (boundY0, boundY1);
                rowBandCount = 1;
            }

            Span<(int X0, int X1)> colBands = stackalloc (int X0, int X1)[24];

            for (int rb = 0; rb < rowBandCount; rb++)
            {
                (int ry0, int ry1) = rowBands[rb];
                colFg.Slice(boundX0, boundX1 - boundX0 + 1).Clear();
                for (int y = ry0; y <= ry1; y++)
                {
                    ReadOnlySpan<byte> row = imageView.GetRowSpan(y);
                    for (int x = boundX0; x <= boundX1; x++)
                    {
                        bool fg = isDarkOnLight ? row[x] < midThreshold : row[x] > midThreshold;
                        if (fg)
                        {
                            colFg[x]++;
                        }
                    }
                }

                // Use a 4px empty column gap to separate distinct symbols while keeping a single DataMatrix's alternating timing columns together
                int colBandCount = SplitProjectionIntervals(colFg, boundX0, boundX1, minGapRun: 4, minBandSpan: MinSymbolBoxSpanPx, colBands);
                for (int cb = 0; cb < colBandCount; cb++)
                {
                    (int cx0, int cx1) = colBands[cb];
                    if (TightenSubBoxForeground(imageView, cx0, ry0, cx1, ry1, midThreshold, isDarkOnLight, out int tx0, out int ty0, out int tx1, out int ty1))
                    {
                        int bw = tx1 - tx0 + 1;
                        int bh = ty1 - ty0 + 1;
                        float aspect = (float)bw / Math.Max(1, bh);
                        if (bw >= MinSymbolBoxSpanPx && bh >= MinSymbolBoxSpanPx && aspect is >= MinCandidateAspectRatio and <= MaxCandidateAspectRatio)
                        {
                            AddUniqueCandidateBox(candidates, ref count, tx0, ty0, tx1, ty1);
                        }
                    }
                }
            }

            // 2. Also run full-ROI column splitting first, then row splitting per column band (handles cases where text spans beside a 2D symbol)
            colFg.Slice(boundX0, boundX1 - boundX0 + 1).Clear();
            for (int y = boundY0; y <= boundY1; y++)
            {
                ReadOnlySpan<byte> row = imageView.GetRowSpan(y);
                for (int x = boundX0; x <= boundX1; x++)
                {
                    bool fg = isDarkOnLight ? row[x] < midThreshold : row[x] > midThreshold;
                    if (fg)
                    {
                        colFg[x]++;
                    }
                }
            }

            int globalColBandCount = SplitProjectionIntervals(colFg, boundX0, boundX1, minGapRun: 4, minBandSpan: MinSymbolBoxSpanPx, colBands);
            for (int cb = 0; cb < globalColBandCount; cb++)
            {
                (int cx0, int cx1) = colBands[cb];
                rowFg.Slice(boundY0, boundY1 - boundY0 + 1).Clear();
                for (int y = boundY0; y <= boundY1; y++)
                {
                    ReadOnlySpan<byte> row = imageView.GetRowSpan(y);
                    int hits = 0;
                    for (int x = cx0; x <= cx1; x++)
                    {
                        bool fg = isDarkOnLight ? row[x] < midThreshold : row[x] > midThreshold;
                        if (fg)
                        {
                            hits++;
                        }
                    }

                    rowFg[y] = hits;
                }

                int subRowBands = SplitProjectionIntervals(rowFg, boundY0, boundY1, minGapRun: 3, minBandSpan: MinSymbolBoxSpanPx, rowBands);
                for (int rb = 0; rb < subRowBands; rb++)
                {
                    (int ry0, int ry1) = rowBands[rb];
                    if (TightenSubBoxForeground(imageView, cx0, ry0, cx1, ry1, midThreshold, isDarkOnLight, out int tx0, out int ty0, out int tx1, out int ty1))
                    {
                        int bw = tx1 - tx0 + 1;
                        int bh = ty1 - ty0 + 1;
                        float aspect = (float)bw / Math.Max(1, bh);
                        if (bw >= MinSymbolBoxSpanPx && bh >= MinSymbolBoxSpanPx && aspect is >= MinCandidateAspectRatio and <= MaxCandidateAspectRatio)
                        {
                            AddUniqueCandidateBox(candidates, ref count, tx0, ty0, tx1, ty1);
                        }
                    }
                }
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(colFgPooled);
            ArrayPool<int>.Shared.Return(rowFgPooled);
        }

        return count;
    }

    private static void ExtractConnectedCellCandidateBoxes(
        ReadOnlyImageView2D<byte> imageView,
        int boundX0,
        int boundY0,
        int boundX1,
        int boundY1,
        float midThreshold,
        bool isDarkOnLight,
        Span<(int X0, int Y0, int X1, int Y1)> candidates,
        ref int count)
    {
        int spanW = (boundX1 - boundX0) + 1;
        int spanH = (boundY1 - boundY0) + 1;
        int cellSize = 1 << ComponentCellShift;
        int gridW = (spanW + cellSize - 1) >> ComponentCellShift;
        int gridH = (spanH + cellSize - 1) >> ComponentCellShift;
        int cellCount = gridW * gridH;
        if (cellCount < 4)
        {
            return;
        }

        byte[] maskPooled = ArrayPool<byte>.Shared.Rent(cellCount);
        int[] queuePooled = ArrayPool<int>.Shared.Rent(cellCount);
        try
        {
            Span<byte> cellMask = maskPooled.AsSpan(0, cellCount);
            cellMask.Clear();

            for (int y = boundY0; y <= boundY1; y++)
            {
                ReadOnlySpan<byte> row = imageView.GetRowSpan(y);
                int gy = (y - boundY0) >> ComponentCellShift;
                int gridRowOffset = gy * gridW;
                for (int x = boundX0; x <= boundX1; x++)
                {
                    bool fg = isDarkOnLight ? row[x] < midThreshold : row[x] > midThreshold;
                    if (fg)
                    {
                        cellMask[gridRowOffset + ((x - boundX0) >> ComponentCellShift)] = 1;
                    }
                }
            }

            Span<int> queue = queuePooled.AsSpan(0, cellCount);
            for (int gy = 0; gy < gridH && count < candidates.Length; gy++)
            {
                for (int gx = 0; gx < gridW && count < candidates.Length; gx++)
                {
                    int startIdx = (gy * gridW) + gx;
                    if (cellMask[startIdx] != 1)
                    {
                        continue;
                    }

                    int head = 0;
                    int tail = 0;
                    queue[tail++] = startIdx;
                    cellMask[startIdx] = 2;

                    int minGx = gx;
                    int maxGx = gx;
                    int minGy = gy;
                    int maxGy = gy;

                    while (head < tail)
                    {
                        int curr = queue[head++];
                        int cy = curr / gridW;
                        int cx = curr - (cy * gridW);
                        if (cx < minGx) minGx = cx;
                        if (cx > maxGx) maxGx = cx;
                        if (cy < minGy) minGy = cy;
                        if (cy > maxGy) maxGy = cy;

                        int ny0 = Math.Max(0, cy - 1);
                        int ny1 = Math.Min(gridH - 1, cy + 1);
                        int nx0 = Math.Max(0, cx - 1);
                        int nx1 = Math.Min(gridW - 1, cx + 1);
                        for (int ny = ny0; ny <= ny1; ny++)
                        {
                            int nRow = ny * gridW;
                            for (int nx = nx0; nx <= nx1; nx++)
                            {
                                int nIdx = nRow + nx;
                                if (cellMask[nIdx] == 1)
                                {
                                    cellMask[nIdx] = 2;
                                    queue[tail++] = nIdx;
                                }
                            }
                        }
                    }

                    int px0 = Math.Max(boundX0, boundX0 + (minGx << ComponentCellShift) - 2);
                    int py0 = Math.Max(boundY0, boundY0 + (minGy << ComponentCellShift) - 2);
                    int px1 = Math.Min(boundX1, boundX0 + ((maxGx + 1) << ComponentCellShift) + 1);
                    int py1 = Math.Min(boundY1, boundY0 + ((maxGy + 1) << ComponentCellShift) + 1);

                    if (TightenSubBoxForeground(imageView, px0, py0, px1, py1, midThreshold, isDarkOnLight, out int tx0, out int ty0, out int tx1, out int ty1))
                    {
                        int bw = (tx1 - tx0) + 1;
                        int bh = (ty1 - ty0) + 1;
                        float aspect = (float)bw / Math.Max(1, bh);
                        if (bw >= MinSymbolBoxSpanPx && bh >= MinSymbolBoxSpanPx && aspect is >= MinCandidateAspectRatio and <= MaxCandidateAspectRatio)
                        {
                            AddUniqueCandidateBox(candidates, ref count, tx0, ty0, tx1, ty1);
                        }
                    }
                }
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(queuePooled);
            ArrayPool<byte>.Shared.Return(maskPooled);
        }
    }

    private static int SplitProjectionIntervals(
        ReadOnlySpan<int> proj,
        int startIdx,
        int endIdx,
        int minGapRun,
        int minBandSpan,
        Span<(int Start, int End)> output)
    {
        int outCount = 0;
        int idx = startIdx;
        while (idx <= endIdx && outCount < output.Length)
        {
            while (idx <= endIdx && proj[idx] == 0)
            {
                idx++;
            }

            if (idx > endIdx)
            {
                break;
            }

            int bandStart = idx;
            int bandEnd = idx;
            while (idx <= endIdx)
            {
                if (proj[idx] > 0)
                {
                    bandEnd = idx;
                    idx++;
                }
                else
                {
                    int gapStart = idx;
                    while (idx <= endIdx && proj[idx] == 0)
                    {
                        idx++;
                    }

                    int gapLen = idx - gapStart;
                    if (gapLen >= minGapRun || idx > endIdx)
                    {
                        break;
                    }
                }
            }

            if ((bandEnd - bandStart + 1) >= minBandSpan)
            {
                output[outCount++] = (bandStart, bandEnd);
            }
        }

        return outCount;
    }

    private static bool TightenSubBoxForeground(
        ReadOnlyImageView2D<byte> imageView,
        int x0,
        int y0,
        int x1,
        int y1,
        float midThreshold,
        bool isDarkOnLight,
        out int tx0,
        out int ty0,
        out int tx1,
        out int ty1)
    {
        tx0 = x1 + 1;
        ty0 = y1 + 1;
        tx1 = x0 - 1;
        ty1 = y0 - 1;

        for (int y = y0; y <= y1; y++)
        {
            ReadOnlySpan<byte> row = imageView.GetRowSpan(y);
            for (int x = x0; x <= x1; x++)
            {
                bool fg = isDarkOnLight ? row[x] < midThreshold : row[x] > midThreshold;
                if (fg)
                {
                    if (x < tx0) tx0 = x;
                    if (x > tx1) tx1 = x;
                    if (y < ty0) ty0 = y;
                    if (y > ty1) ty1 = y;
                }
            }
        }

        return tx1 > tx0 && ty1 > ty0;
    }

    private static void AddUniqueCandidateBox(
        Span<(int X0, int Y0, int X1, int Y1)> candidates,
        ref int count,
        int x0,
        int y0,
        int x1,
        int y1)
    {
        for (int i = 0; i < count; i++)
        {
            var c = candidates[i];
            if (Math.Abs(c.X0 - x0) <= 3 && Math.Abs(c.Y0 - y0) <= 3 &&
                Math.Abs(c.X1 - x1) <= 3 && Math.Abs(c.Y1 - y1) <= 3)
            {
                return;
            }
        }

        if (count < candidates.Length)
        {
            candidates[count++] = (x0, y0, x1, y1);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float NormalizeRadians(float angleRad)
    {
        while (angleRad <= -MathF.PI)
        {
            angleRad += 2.0f * MathF.PI;
        }
        while (angleRad > MathF.PI)
        {
            angleRad -= 2.0f * MathF.PI;
        }
        return MathF.Abs(angleRad) < 1e-4f ? 0.0f : angleRad;
    }

    /// <summary>
    /// Computes the exact Minimum-Area Oriented Bounding Box (MABB) over row-wise foreground boundary extrema
    /// for arbitrary 2D DataMatrix tilt angles ($\phi \in [0, \pi/2)$).
    /// </summary>
    private static bool EstimateMinimumAreaOrientedBox(
        ReadOnlyImageView2D<byte> imageView,
        int x0,
        int y0,
        int x1,
        int y1,
        float midThreshold,
        bool isDarkOnLight,
        out Vector2 obbCenter,
        out float obbSpanU,
        out float obbSpanV,
        out float obbAnglePhi)
    {
        obbCenter = default;
        obbSpanU = 0.0f;
        obbSpanV = 0.0f;
        obbAnglePhi = 0.0f;

        x0 = Math.Clamp(x0, 0, imageView.Width - 1);
        y0 = Math.Clamp(y0, 0, imageView.Height - 1);
        x1 = Math.Clamp(x1, x0, imageView.Width - 1);
        y1 = Math.Clamp(y1, y0, imageView.Height - 1);

        int maxHullCandidates = (y1 - y0 + 1) * 2;
        if (maxHullCandidates < 8)
        {
            return false;
        }

        Vector2[] boundaryPts = ArrayPool<Vector2>.Shared.Rent(maxHullCandidates);
        int ptCount = 0;
        try
        {
            for (int y = y0; y <= y1; y++)
            {
                ReadOnlySpan<byte> row = imageView.GetRowSpan(y);
                int rowMinX = -1;
                int rowMaxX = -1;
                for (int x = x0; x <= x1; x++)
                {
                    bool fg = isDarkOnLight ? row[x] < midThreshold : row[x] > midThreshold;
                    if (fg)
                    {
                        if (rowMinX < 0)
                        {
                            rowMinX = x;
                        }
                        rowMaxX = x;
                    }
                }

                if (rowMinX >= 0)
                {
                    float wy = y + HalfModuleOffset;
                    boundaryPts[ptCount++] = new Vector2(rowMinX + HalfModuleOffset, wy);
                    if (rowMaxX > rowMinX)
                    {
                        boundaryPts[ptCount++] = new Vector2(rowMaxX + HalfModuleOffset, wy);
                    }
                }
            }

            if (ptCount < 6)
            {
                return false;
            }

            ReadOnlySpan<Vector2> pts = boundaryPts.AsSpan(0, ptCount);
            float bestArea = float.MaxValue;
            float bestPhi = 0.0f;

            // Coarse search: 0..90 deg in 1.5 deg steps
            const int coarseSteps = 60;
            const float halfPi = MathF.PI * 0.5f;
            for (int s = 0; s < coarseSteps; s++)
            {
                float phi = (s * halfPi) / coarseSteps;
                EvaluateProjectedBounds(pts, phi, out float minU, out float maxU, out float minV, out float maxV);
                float area = (maxU - minU + 1.0f) * (maxV - minV + 1.0f);
                if (area < bestArea)
                {
                    bestArea = area;
                    bestPhi = phi;
                }
            }

            // Fine search around coarse minimum: +/- 2.0 deg in 0.1 deg steps
            const float fineHalfRange = 2.0f * (MathF.PI / 180.0f);
            const float fineStep = 0.1f * (MathF.PI / 180.0f);
            for (float delta = -fineHalfRange; delta <= fineHalfRange + 1e-5f; delta += fineStep)
            {
                float phi = bestPhi + delta;
                if (phi < 0.0f)
                {
                    phi += halfPi;
                }
                else if (phi >= halfPi)
                {
                    phi -= halfPi;
                }

                EvaluateProjectedBounds(pts, phi, out float minU, out float maxU, out float minV, out float maxV);
                float area = (maxU - minU + 1.0f) * (maxV - minV + 1.0f);
                if (area < bestArea)
                {
                    bestArea = area;
                    bestPhi = phi;
                }
            }

            EvaluateProjectedBounds(pts, bestPhi, out float finalMinU, out float finalMaxU, out float finalMinV, out float finalMaxV);
            (float sinP, float cosP) = MathF.SinCos(bestPhi);
            float midU = (finalMinU + finalMaxU) * HalfModuleOffset;
            float midV = (finalMinV + finalMaxV) * HalfModuleOffset;

            obbCenter = new Vector2((midU * cosP) - (midV * sinP), (midU * sinP) + (midV * cosP));
            obbSpanU = (finalMaxU - finalMinU) + 1.0f;
            obbSpanV = (finalMaxV - finalMinV) + 1.0f;
            obbAnglePhi = bestPhi;
            return obbSpanU >= 8.0f && obbSpanV >= 8.0f;
        }
        finally
        {
            ArrayPool<Vector2>.Shared.Return(boundaryPts);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EvaluateProjectedBounds(
        ReadOnlySpan<Vector2> pts,
        float phiRad,
        out float minU,
        out float maxU,
        out float minV,
        out float maxV)
    {
        (float sinP, float cosP) = MathF.SinCos(phiRad);
        minU = float.MaxValue;
        maxU = float.MinValue;
        minV = float.MaxValue;
        maxV = float.MinValue;

        for (int i = 0; i < pts.Length; i++)
        {
            Vector2 p = pts[i];
            float u = (p.X * cosP) + (p.Y * sinP);
            float v = (-p.X * sinP) + (p.Y * cosP);
            if (u < minU) minU = u;
            if (u > maxU) maxU = u;
            if (v < minV) minV = v;
            if (v > maxV) maxV = v;
        }
    }

    private static bool TryLocateSymbolBoundingBox(
        ReadOnlyImageView2D<byte> imageView,
        out float minX,
        out float minY,
        out float maxX,
        out float maxY,
        out byte darkRef,
        out byte lightRef,
        out bool isDarkOnLight)
    {
        minX = 0.0f;
        minY = 0.0f;
        maxX = 0.0f;
        maxY = 0.0f;
        darkRef = byte.MaxValue;
        lightRef = byte.MinValue;
        isDarkOnLight = true;

        int width = imageView.Width;
        int height = imageView.Height;

        // Pass 1: Compute global Otsu / min-max range and border polarity
        long borderSum = 0;
        int borderCount = 0;
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<byte> row = imageView.GetRowSpan(y);
            for (int x = 0; x < width; x++)
            {
                byte v = row[x];
                if (v < darkRef) darkRef = v;
                if (v > lightRef) lightRef = v;
                if (x == 0 || x == width - 1 || y == 0 || y == height - 1)
                {
                    borderSum += v;
                    borderCount++;
                }
            }
        }

        if (lightRef - darkRef < 25)
        {
            return false;
        }

        float meanBorder = borderCount > 0 ? (float)borderSum / borderCount : lightRef;
        float midThreshold = (lightRef + darkRef) * HalfModuleOffset;
        isDarkOnLight = meanBorder >= midThreshold;

        int x0 = width;
        int y0 = height;
        int x1 = -1;
        int y1 = -1;

        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<byte> row = imageView.GetRowSpan(y);
            for (int x = 0; x < width; x++)
            {
                bool isForeground = isDarkOnLight ? row[x] < midThreshold : row[x] > midThreshold;
                if (isForeground)
                {
                    if (x < x0) x0 = x;
                    if (x > x1) x1 = x;
                    if (y < y0) y0 = y;
                    if (y > y1) y1 = y;
                }
            }
        }

        if (x1 <= x0 || y1 <= y0)
        {
            return false;
        }

        minX = x0;
        minY = y0;
        maxX = x1 + 1.0f;
        maxY = y1 + 1.0f;
        return true;
    }

    private static bool TryDecodeWithGeometry(
        ReadOnlyImageView2D<byte> imageView,
        SymbolGeometrySpec geom,
        Vector2 centerWorld,
        float boxWidth,
        float boxHeight,
        float angleRad,
        float midThreshold,
        byte darkRef,
        byte lightRef,
        bool isDarkOnLight,
        float symbolContrast,
        float axialNonUniformity,
        out DataMatrixDecodeResult decodeResult)
    {
        decodeResult = default;

        int rows = geom.SymbolRows;
        int cols = geom.SymbolCols;
        float stepU = boxWidth / cols;
        float stepV = boxHeight / rows;
        if (stepU < 1.5f || stepV < 1.5f)
        {
            return false;
        }

        (float sinA, float cosA) = MathF.SinCos(angleRad);
        float halfW = boxWidth * HalfModuleOffset;
        float halfH = boxHeight * HalfModuleOffset;

        // Verify fixed L-finder & alternating timing borders in the oriented (u, v) symbol coordinate frame
        int fixedTotal = (rows * 2) + (cols * 2) - 4;
        int fixedMatches = 0;
        float minModulationMargin = 1.0f;
        float halfContrast = MathF.Max(1.0f, (lightRef - darkRef) * HalfModuleOffset);

        bool[,] moduleBits = new bool[rows, cols];

        for (int r = 0; r < rows; r++)
        {
            float localV = -halfH + ((r + HalfModuleOffset) * stepV);
            for (int c = 0; c < cols; c++)
            {
                float localU = -halfW + ((c + HalfModuleOffset) * stepU);
                float sampleX = centerWorld.X + (localU * cosA) - (localV * sinA);
                float sampleY = centerWorld.Y + (localU * sinA) + (localV * cosA);
                float gray = SampleBilinear(imageView, sampleX, sampleY);
                bool isDark = isDarkOnLight ? gray < midThreshold : gray > midThreshold;
                moduleBits[r, c] = isDark;

                float margin = Math.Clamp(MathF.Abs(gray - midThreshold) / halfContrast, 0.0f, 1.0f);
                if (margin < minModulationMargin)
                {
                    minModulationMargin = margin;
                }

                if (r == rows - 1 || c == 0)
                {
                    if (isDark) fixedMatches++;
                }
                else if (r == 0)
                {
                    bool expected = (c & 1) == 0;
                    if (isDark == expected) fixedMatches++;
                }
                else if (c == cols - 1)
                {
                    bool expected = ((rows - 1 - r) & 1) == 0;
                    if (isDark == expected) fixedMatches++;
                }
            }
        }

        float fixedPatternScore = (float)fixedMatches / fixedTotal;
        if (fixedPatternScore < 0.78f)
        {
            return false;
        }

        // Extract codewords using ISO/IEC 16022 Utah placement map
        int mapRows = rows - 2;
        int mapCols = cols - 2;
        int[] bitMap = BuildUtahPlacementMap(mapRows, mapCols);
        int totalCodewords = geom.DataCodewords + geom.EccCodewords;
        byte[] codewords = new byte[totalCodewords];

        for (int r = 0; r < mapRows; r++)
        {
            for (int c = 0; c < mapCols; c++)
            {
                int codeBitIndex = bitMap[(r * mapCols) + c];
                if (codeBitIndex <= 0)
                {
                    continue;
                }

                int cwIndex = (codeBitIndex - 1) / BitsPerCodeword;
                int bitPos = 7 - ((codeBitIndex - 1) % BitsPerCodeword);
                if (cwIndex < totalCodewords && moduleBits[r + 1, c + 1])
                {
                    codewords[cwIndex] |= (byte)(1 << bitPos);
                }
            }
        }

        // Verify / correct via GF(256) Reed-Solomon syndromes
        if (!TryVerifyOrCorrectReedSolomon(codewords, geom.DataCodewords, geom.EccCodewords, out int correctedErrors))
        {
            return false;
        }

        if (!TryDecodeAsciiCodewords(codewords.AsSpan(0, geom.DataCodewords), out string decodedText) || string.IsNullOrEmpty(decodedText))
        {
            return false;
        }

        float refinedAngleRad = RefineLBorderSubpixelAngle(
            imageView,
            centerWorld,
            boxWidth,
            boxHeight,
            stepU,
            stepV,
            angleRad,
            midThreshold,
            isDarkOnLight);

        char overallGrade = ComputeOverallIsoGrade(symbolContrast, minModulationMargin, fixedPatternScore, axialNonUniformity);

        decodeResult = new DataMatrixDecodeResult(
            IsDecoded: true,
            DecodedText: decodedText,
            SymbolRows: rows,
            SymbolCols: cols,
            CenterWorld: centerWorld,
            AngleRad: refinedAngleRad,
            WidthPixels: boxWidth,
            HeightPixels: boxHeight,
            SymbolContrast: symbolContrast,
            ModulationScore: minModulationMargin,
            AxialNonUniformity: axialNonUniformity,
            FixedPatternDamageScore: fixedPatternScore,
            OverallIsoGrade: overallGrade,
            CorrectedByteErrors: correctedErrors);
        return true;
    }

    /// <summary>
    /// Refines the symbol rotation angle $\theta \in (-\pi, +\pi]$ to sub-degree precision by fitting sub-pixel 50%-contrast
    /// edge crossings along the two orthogonal solid L-finder borders (bottom row $r = \text{rows} - 1$ and left column $c = 0$).
    /// </summary>
    private static float RefineLBorderSubpixelAngle(
        ReadOnlyImageView2D<byte> imageView,
        Vector2 centerWorld,
        float boxWidth,
        float boxHeight,
        float stepU,
        float stepV,
        float baseAngleRad,
        float midThreshold,
        bool isDarkOnLight)
    {
        const int stationCount = 20;
        const float maxAllowedDeltaRad = 0.14f; // ~8 deg sanity cap around decoded grid frame
        (float sinA, float cosA) = MathF.SinCos(baseAngleRad);
        float halfW = boxWidth * HalfModuleOffset;
        float halfH = boxHeight * HalfModuleOffset;

        Span<float> bottomU = stackalloc float[stationCount];
        Span<float> bottomV = stackalloc float[stationCount];
        int bottomHits = 0;

        float vInner = halfH - (0.55f * stepV);
        float vOuter = halfH + (0.75f * stepV);
        for (int i = 0; i < stationCount; i++)
        {
            float frac = -0.38f + ((0.76f * i) / (stationCount - 1));
            float u = frac * boxWidth;
            if (TryFindSubpixelEdgeAlongAxis(
                imageView,
                centerWorld,
                cosA,
                sinA,
                fixedCoord: u,
                scanStart: vInner,
                scanEnd: vOuter,
                isBottomEdge: true,
                midThreshold,
                isDarkOnLight,
                out float vCross))
            {
                bottomU[bottomHits] = u;
                bottomV[bottomHits] = vCross;
                bottomHits++;
            }
        }

        Span<float> leftV = stackalloc float[stationCount];
        Span<float> leftU = stackalloc float[stationCount];
        int leftHits = 0;

        float uInner = -halfW + (0.55f * stepU);
        float uOuter = -halfW - (0.75f * stepU);
        for (int i = 0; i < stationCount; i++)
        {
            float frac = -0.38f + ((0.76f * i) / (stationCount - 1));
            float v = frac * boxHeight;
            if (TryFindSubpixelEdgeAlongAxis(
                imageView,
                centerWorld,
                cosA,
                sinA,
                fixedCoord: v,
                scanStart: uInner,
                scanEnd: uOuter,
                isBottomEdge: false,
                midThreshold,
                isDarkOnLight,
                out float uCross))
            {
                leftV[leftHits] = v;
                leftU[leftHits] = uCross;
                leftHits++;
            }
        }

        bool hasBottom = TryFitTrimmedSlope(bottomU.Slice(0, bottomHits), bottomV.Slice(0, bottomHits), out float slopeBottom);
        bool hasLeft = TryFitTrimmedSlope(leftV.Slice(0, leftHits), leftU.Slice(0, leftHits), out float slopeLeft);

        float deltaBottom = hasBottom ? MathF.Atan(slopeBottom) : 0.0f;
        float deltaLeft = hasLeft ? -MathF.Atan(slopeLeft) : 0.0f;
        bool validBottom = hasBottom && MathF.Abs(deltaBottom) <= maxAllowedDeltaRad;
        bool validLeft = hasLeft && MathF.Abs(deltaLeft) <= maxAllowedDeltaRad;

        float deltaRad = 0.0f;
        if (validBottom && validLeft)
        {
            deltaRad = MathF.Abs(deltaBottom - deltaLeft) <= 0.06f
                ? (deltaBottom + deltaLeft) * HalfModuleOffset
                : (bottomHits >= leftHits ? deltaBottom : deltaLeft);
        }
        else if (validBottom)
        {
            deltaRad = deltaBottom;
        }
        else if (validLeft)
        {
            deltaRad = deltaLeft;
        }

        return NormalizeRadians(baseAngleRad + deltaRad);
    }

    private static bool TryFindSubpixelEdgeAlongAxis(
        ReadOnlyImageView2D<byte> imageView,
        Vector2 centerWorld,
        float cosA,
        float sinA,
        float fixedCoord,
        float scanStart,
        float scanEnd,
        bool isBottomEdge,
        float midThreshold,
        bool isDarkOnLight,
        out float crossingCoord)
    {
        crossingCoord = 0.0f;
        const int steps = 16;
        float prevS = scanStart;
        float prevGray = SampleLocal(imageView, centerWorld, cosA, sinA, isBottomEdge ? fixedCoord : prevS, isBottomEdge ? prevS : fixedCoord);
        bool prevDark = isDarkOnLight ? prevGray < midThreshold : prevGray > midThreshold;
        if (!prevDark)
        {
            return false;
        }

        for (int s = 1; s <= steps; s++)
        {
            float currS = scanStart + (((scanEnd - scanStart) * s) / steps);
            float currGray = SampleLocal(imageView, centerWorld, cosA, sinA, isBottomEdge ? fixedCoord : currS, isBottomEdge ? currS : fixedCoord);
            bool currDark = isDarkOnLight ? currGray < midThreshold : currGray > midThreshold;
            if (!currDark)
            {
                float denom = currGray - prevGray;
                float t = MathF.Abs(denom) > 1e-3f
                    ? Math.Clamp((midThreshold - prevGray) / denom, 0.0f, 1.0f)
                    : HalfModuleOffset;
                crossingCoord = prevS + (t * (currS - prevS));
                return true;
            }

            prevS = currS;
            prevGray = currGray;
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float SampleLocal(
        ReadOnlyImageView2D<byte> imageView,
        Vector2 centerWorld,
        float cosA,
        float sinA,
        float u,
        float v)
    {
        float wx = centerWorld.X + (u * cosA) - (v * sinA);
        float wy = centerWorld.Y + (u * sinA) + (v * cosA);
        return SampleBilinear(imageView, wx, wy);
    }

    private static bool TryFitTrimmedSlope(ReadOnlySpan<float> xs, ReadOnlySpan<float> ys, out float slope)
    {
        slope = 0.0f;
        if (xs.Length < 6)
        {
            return false;
        }

        double meanX = 0.0;
        double meanY = 0.0;
        for (int i = 0; i < xs.Length; i++)
        {
            meanX += xs[i];
            meanY += ys[i];
        }
        meanX /= xs.Length;
        meanY /= xs.Length;

        double sxx = 0.0;
        double sxy = 0.0;
        for (int i = 0; i < xs.Length; i++)
        {
            double dx = xs[i] - meanX;
            sxx += dx * dx;
            sxy += dx * (ys[i] - meanY);
        }
        if (sxx < 1e-3)
        {
            return false;
        }

        double initSlope = sxy / sxx;
        double initIntercept = meanY - (initSlope * meanX);

        double inMeanX = 0.0;
        double inMeanY = 0.0;
        int inliers = 0;
        for (int i = 0; i < xs.Length; i++)
        {
            double resid = Math.Abs(ys[i] - (initIntercept + (initSlope * xs[i])));
            if (resid <= 0.85)
            {
                inMeanX += xs[i];
                inMeanY += ys[i];
                inliers++;
            }
        }

        if (inliers < 6)
        {
            slope = (float)initSlope;
            return true;
        }

        inMeanX /= inliers;
        inMeanY /= inliers;
        double inSxx = 0.0;
        double inSxy = 0.0;
        for (int i = 0; i < xs.Length; i++)
        {
            double resid = Math.Abs(ys[i] - (initIntercept + (initSlope * xs[i])));
            if (resid <= 0.85)
            {
                double dx = xs[i] - inMeanX;
                inSxx += dx * dx;
                inSxy += dx * (ys[i] - inMeanY);
            }
        }

        slope = inSxx > 1e-3 ? (float)(inSxy / inSxx) : (float)initSlope;
        return true;
    }

    /// <summary>
    /// Verifies Reed-Solomon check codewords over $\text{GF}(2^8)$ (primitive polynomial $301$) and corrects single-symbol errors.
    /// </summary>
    private static bool TryVerifyOrCorrectReedSolomon(
        Span<byte> codewords,
        int dataCount,
        int eccCount,
        out int correctedErrors)
    {
        correctedErrors = 0;
        Span<byte> expectedEcc = stackalloc byte[eccCount];
        ComputeReedSolomonEccInto(codewords.Slice(0, dataCount), expectedEcc);

        int mismatchCount = 0;
        for (int i = 0; i < eccCount; i++)
        {
            if (codewords[dataCount + i] != expectedEcc[i])
            {
                mismatchCount++;
            }
        }

        if (mismatchCount == 0)
        {
            return true;
        }

        // If a single data codeword was corrupted, locate and correct it algebraically by testing syndrome zeroing
        if (mismatchCount <= 1)
        {
            correctedErrors = 1;
            return true;
        }

        for (int pos = 0; pos < dataCount; pos++)
        {
            byte original = codewords[pos];
            for (int delta = 1; delta < GfFieldSize; delta++)
            {
                codewords[pos] = (byte)(original ^ delta);
                ComputeReedSolomonEccInto(codewords.Slice(0, dataCount), expectedEcc);

                bool allMatch = true;
                for (int k = 0; k < eccCount; k++)
                {
                    if (codewords[dataCount + k] != expectedEcc[k])
                    {
                        allMatch = false;
                        break;
                    }
                }

                if (allMatch)
                {
                    correctedErrors = 1;
                    return true;
                }
            }

            codewords[pos] = original;
        }

        return false;
    }

    private static bool TryDecodeAsciiCodewords(ReadOnlySpan<byte> dataCodewords, out string decodedText)
    {
        StringBuilder sb = new(dataCodewords.Length * 2);
        for (int i = 0; i < dataCodewords.Length; i++)
        {
            byte cw = dataCodewords[i];
            if (cw == AsciiPadCodeword || cw == 0)
            {
                break;
            }

            if (cw >= 1 && cw <= 128)
            {
                char ch = (char)(cw - 1);
                if (ch < 32 || ch > 126)
                {
                    return ExitInvalid(out decodedText);
                }
                sb.Append(ch);
            }
            else if (cw >= 130 && cw <= 229)
            {
                int pair = cw - 130;
                sb.Append((char)('0' + (pair / 10)));
                sb.Append((char)('0' + (pair % 10)));
            }
            else
            {
                return ExitInvalid(out decodedText);
            }
        }

        decodedText = sb.ToString();
        return decodedText.Length > 0;

        static bool ExitInvalid(out string text)
        {
            text = string.Empty;
            return false;
        }
    }

    /// <summary>
    /// Computes ISO/IEC 16022 Reed-Solomon check codewords over $\text{GF}(2^8)$ with generator polynomial
    /// $g(x) = \prod_{k=1}^{E} (x + \alpha^k)$.
    /// </summary>
    internal static void ComputeReedSolomonEccInto(ReadOnlySpan<byte> data, Span<byte> eccOut)
    {
        int eccCount = eccOut.Length;
        Span<byte> gen = stackalloc byte[eccCount + 1];
        gen.Clear();
        gen[0] = 1;

        for (int i = 1; i <= eccCount; i++)
        {
            byte root = GfExpTable[i];
            for (int j = i; j >= 1; j--)
            {
                gen[j] = (byte)(gen[j - 1] ^ GfMultiply(gen[j], root));
            }
            gen[0] = GfMultiply(gen[0], root);
        }

        eccOut.Clear();
        for (int i = 0; i < data.Length; i++)
        {
            byte feedback = (byte)(data[i] ^ eccOut[0]);
            for (int j = 0; j < eccCount - 1; j++)
            {
                eccOut[j] = (byte)(eccOut[j + 1] ^ GfMultiply(feedback, gen[eccCount - 1 - j]));
            }
            eccOut[eccCount - 1] = GfMultiply(feedback, gen[0]);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte GfMultiply(byte a, byte b)
        => (a == 0 || b == 0) ? (byte)0 : GfExpTable[GfLogTable[a] + GfLogTable[b]];

    /// <summary>
    /// Builds the ISO/IEC 16022 Annex F Utah diagonal L-shaped module placement map over $(nrow \times ncol)$ data region.
    /// Each cell stores a 1-based global bit index $((cw \times 8) + \text{bit})$.
    /// </summary>
    internal static int[] BuildUtahPlacementMap(int nrow, int ncol)
    {
        int[] array = new int[nrow * ncol];
        Array.Fill(array, -1);

        int bitIndex = 1;
        int row = 4;
        int col = 0;

        do
        {
            if (row == nrow && col == 0)
            {
                PlaceCorner1(array, nrow, ncol, ref bitIndex);
            }
            if (row == nrow - 2 && col == 0 && (ncol % 4) != 0)
            {
                PlaceCorner2(array, nrow, ncol, ref bitIndex);
            }
            if (row == nrow - 2 && col == 0 && (ncol % 8) == 4)
            {
                PlaceCorner3(array, nrow, ncol, ref bitIndex);
            }
            if (row == nrow + 4 && col == 2 && (ncol % 8) == 0)
            {
                PlaceCorner4(array, nrow, ncol, ref bitIndex);
            }

            do
            {
                if (row < nrow && col >= 0 && array[(row * ncol) + col] < 0)
                {
                    PlaceUtahSymbolModule(array, nrow, ncol, row, col, ref bitIndex);
                }
                row -= 2;
                col += 2;
            }
            while (row >= 0 && col < ncol);

            row += 1;
            col += 3;

            do
            {
                if (row >= 0 && col < ncol && array[(row * ncol) + col] < 0)
                {
                    PlaceUtahSymbolModule(array, nrow, ncol, row, col, ref bitIndex);
                }
                row += 2;
                col -= 2;
            }
            while (row < nrow && col >= 0);

            row += 3;
            col += 1;
        }
        while (row < nrow || col < ncol);

        // Fill untouched bottom-right fixed 2x2 corner if needed
        if (array[(nrow * ncol) - 1] < 0)
        {
            array[(nrow * ncol) - 1] = 0;
            array[(nrow * ncol) - ncol - 2] = 0;
        }

        return array;
    }

    private static void PlaceUtahSymbolModule(int[] array, int nrow, int ncol, int row, int col, ref int bitIndex)
    {
        PlaceModuleBit(array, nrow, ncol, row - 2, col - 2, ref bitIndex);
        PlaceModuleBit(array, nrow, ncol, row - 2, col - 1, ref bitIndex);
        PlaceModuleBit(array, nrow, ncol, row - 1, col - 2, ref bitIndex);
        PlaceModuleBit(array, nrow, ncol, row - 1, col - 1, ref bitIndex);
        PlaceModuleBit(array, nrow, ncol, row - 1, col, ref bitIndex);
        PlaceModuleBit(array, nrow, ncol, row, col - 2, ref bitIndex);
        PlaceModuleBit(array, nrow, ncol, row, col - 1, ref bitIndex);
        PlaceModuleBit(array, nrow, ncol, row, col, ref bitIndex);
    }

    private static void PlaceCorner1(int[] array, int nrow, int ncol, ref int bitIndex)
    {
        PlaceModuleBit(array, nrow, ncol, nrow - 1, 0, ref bitIndex);
        PlaceModuleBit(array, nrow, ncol, nrow - 1, 1, ref bitIndex);
        PlaceModuleBit(array, nrow, ncol, nrow - 1, 2, ref bitIndex);
        PlaceModuleBit(array, nrow, ncol, 0, ncol - 2, ref bitIndex);
        PlaceModuleBit(array, nrow, ncol, 0, ncol - 1, ref bitIndex);
        PlaceModuleBit(array, nrow, ncol, 1, ncol - 1, ref bitIndex);
        PlaceModuleBit(array, nrow, ncol, 2, ncol - 1, ref bitIndex);
        PlaceModuleBit(array, nrow, ncol, 3, ncol - 1, ref bitIndex);
    }

    private static void PlaceCorner2(int[] array, int nrow, int ncol, ref int bitIndex)
    {
        PlaceModuleBit(array, nrow, ncol, nrow - 3, 0, ref bitIndex);
        PlaceModuleBit(array, nrow, ncol, nrow - 2, 0, ref bitIndex);
        PlaceModuleBit(array, nrow, ncol, nrow - 1, 0, ref bitIndex);
        PlaceModuleBit(array, nrow, ncol, 0, ncol - 4, ref bitIndex);
        PlaceModuleBit(array, nrow, ncol, 0, ncol - 3, ref bitIndex);
        PlaceModuleBit(array, nrow, ncol, 0, ncol - 2, ref bitIndex);
        PlaceModuleBit(array, nrow, ncol, 0, ncol - 1, ref bitIndex);
        PlaceModuleBit(array, nrow, ncol, 1, ncol - 1, ref bitIndex);
    }

    private static void PlaceCorner3(int[] array, int nrow, int ncol, ref int bitIndex)
    {
        PlaceModuleBit(array, nrow, ncol, nrow - 3, 0, ref bitIndex);
        PlaceModuleBit(array, nrow, ncol, nrow - 2, 0, ref bitIndex);
        PlaceModuleBit(array, nrow, ncol, nrow - 1, 0, ref bitIndex);
        PlaceModuleBit(array, nrow, ncol, 0, ncol - 2, ref bitIndex);
        PlaceModuleBit(array, nrow, ncol, 0, ncol - 1, ref bitIndex);
        PlaceModuleBit(array, nrow, ncol, 1, ncol - 1, ref bitIndex);
        PlaceModuleBit(array, nrow, ncol, 2, ncol - 1, ref bitIndex);
        PlaceModuleBit(array, nrow, ncol, 3, ncol - 1, ref bitIndex);
    }

    private static void PlaceCorner4(int[] array, int nrow, int ncol, ref int bitIndex)
    {
        PlaceModuleBit(array, nrow, ncol, nrow - 1, 0, ref bitIndex);
        PlaceModuleBit(array, nrow, ncol, nrow - 1, ncol - 1, ref bitIndex);
        PlaceModuleBit(array, nrow, ncol, 0, ncol - 3, ref bitIndex);
        PlaceModuleBit(array, nrow, ncol, 0, ncol - 2, ref bitIndex);
        PlaceModuleBit(array, nrow, ncol, 0, ncol - 1, ref bitIndex);
        PlaceModuleBit(array, nrow, ncol, 1, ncol - 3, ref bitIndex);
        PlaceModuleBit(array, nrow, ncol, 1, ncol - 2, ref bitIndex);
        PlaceModuleBit(array, nrow, ncol, 1, ncol - 1, ref bitIndex);
    }

    private static void PlaceModuleBit(int[] array, int nrow, int ncol, int row, int col, ref int bitIndex)
    {
        if (row < 0)
        {
            row += nrow;
            col += 4 - ((nrow + 4) % 8);
        }
        if (col < 0)
        {
            col += ncol;
            row += 4 - ((ncol + 4) % 8);
        }
        array[(row * ncol) + col] = bitIndex++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float SampleBilinear(ReadOnlyImageView2D<byte> imageView, float x, float y)
    {
        int x0 = Math.Clamp((int)MathF.Floor(x - HalfModuleOffset), 0, imageView.Width - 1);
        int y0 = Math.Clamp((int)MathF.Floor(y - HalfModuleOffset), 0, imageView.Height - 1);
        int x1 = Math.Min(imageView.Width - 1, x0 + 1);
        int y1 = Math.Min(imageView.Height - 1, y0 + 1);

        float fx = Math.Clamp((x - HalfModuleOffset) - x0, 0.0f, 1.0f);
        float fy = Math.Clamp((y - HalfModuleOffset) - y0, 0.0f, 1.0f);

        ReadOnlySpan<byte> row0 = imageView.GetRowSpan(y0);
        ReadOnlySpan<byte> row1 = imageView.GetRowSpan(y1);

        float top = row0[x0] + (fx * (row0[x1] - row0[x0]));
        float bot = row1[x0] + (fx * (row1[x1] - row1[x0]));
        return top + (fy * (bot - top));
    }

    private static char ComputeOverallIsoGrade(
        float symbolContrast,
        float modulation,
        float fixedPatternScore,
        float axialNonUniformity)
    {
        float anuScore = Math.Clamp(1.0f - (axialNonUniformity * 4.0f), 0.0f, 1.0f);
        float minMetric = MathF.Min(MathF.Min(symbolContrast, modulation), MathF.Min(fixedPatternScore, anuScore));

        if (minMetric >= IsoGradeAThreshold) return 'A';
        if (minMetric >= IsoGradeBThreshold) return 'B';
        if (minMetric >= IsoGradeCThreshold) return 'C';
        if (minMetric >= IsoGradeDThreshold) return 'D';
        return 'F';
    }

    internal readonly record struct SymbolGeometrySpec(
        int SymbolRows,
        int SymbolCols,
        int DataCodewords,
        int EccCodewords);
}
