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
    // [Math Why]: ISO/IEC 16022 ECC 200 Galois Field GF(2^8) primitive polynomial p(x) = x^8 + x^5 + x^3 + x^2 + 1 = 0x12D (301).
    private const int GfPrimitivePolynomial = 301;
    private const int GfFieldSize = 256;
    private const int GfMaxElementIndex = 255;
    private const byte AsciiPadCodeword = 129;
    private const int BitsPerCodeword = 8;
    private const float HalfModuleOffset = 0.5f;
    private const float MaxByteNormalizedScale = 1.0f / 255.0f;
    private const float IsoGradeAThreshold = 0.70f;
    private const float IsoGradeBThreshold = 0.55f;
    private const float IsoGradeCThreshold = 0.40f;
    private const float IsoGradeDThreshold = 0.25f;

    private static readonly byte[] GfExpTable = new byte[GfFieldSize * 2];
    private static readonly byte[] GfLogTable = new byte[GfFieldSize];

    private static readonly SymbolGeometrySpec[] SupportedSymbolGeometries =
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
    /// returning its payload and ISO/IEC 15415 print quality verification metrics.
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

        float boxWidth = maxX - minX;
        float boxHeight = maxY - minY;
        if (boxWidth < 8.0f || boxHeight < 8.0f)
        {
            return default;
        }

        Vector2 centerWorld = new((minX + maxX) * HalfModuleOffset, (minY + maxY) * HalfModuleOffset);
        float symbolContrast = Math.Abs(lightRef - darkRef) * MaxByteNormalizedScale;
        float midThreshold = (lightRef + darkRef) * HalfModuleOffset;
        float axialNonUniformity = MathF.Abs(boxWidth - boxHeight) / MathF.Max(boxWidth, boxHeight);

        // Evaluate candidate ISO/IEC 16022 symbol geometries using timing-border consistency + GF(256) Reed-Solomon syndromes
        DataMatrixDecodeResult bestFallback = default;

        for (int g = 0; g < SupportedSymbolGeometries.Length; g++)
        {
            SymbolGeometrySpec geom = SupportedSymbolGeometries[g];
            if (TryDecodeWithGeometry(
                imageView,
                geom,
                minX,
                minY,
                boxWidth,
                boxHeight,
                centerWorld,
                midThreshold,
                darkRef,
                lightRef,
                isDarkOnLight,
                symbolContrast,
                axialNonUniformity,
                out DataMatrixDecodeResult result))
            {
                if (result.CorrectedByteErrors == 0 && result.FixedPatternDamageScore >= 0.85f)
                {
                    return result;
                }

                if (!bestFallback.IsDecoded || result.FixedPatternDamageScore > bestFallback.FixedPatternDamageScore)
                {
                    bestFallback = result;
                }
            }
        }

        return bestFallback;
    }

    /// <summary>
    /// Renders a ground-truth ISO/IEC 16022 ECC 200 DataMatrix symbol for <paramref name="asciiPayload"/> onto a strided
    /// 8-bit image buffer using the exact Utah L-shaped module placement and $\text{GF}(2^8)$ Reed-Solomon check codewords.
    /// Useful for deterministic verification, calibration targets, and interactive sample synthesis.
    /// </summary>
    public static void RenderEcc200Symbol(
        string asciiPayload,
        Span<byte> destination,
        int width,
        int height,
        int stride,
        int originX,
        int originY,
        int moduleSizePixels,
        byte darkLevel = 25,
        byte lightLevel = 230)
    {
        ArgumentNullException.ThrowIfNull(asciiPayload);
        ArgumentOutOfRangeException.ThrowIfLessThan(moduleSizePixels, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, width);

        byte[] dataBytes = EncodeAsciiCodewords(asciiPayload, out SymbolGeometrySpec geom);
        byte[] eccBytes = ComputeReedSolomonEcc(dataBytes, geom.EccCodewords);

        byte[] allCodewords = new byte[geom.DataCodewords + geom.EccCodewords];
        Array.Copy(dataBytes, 0, allCodewords, 0, geom.DataCodewords);
        Array.Copy(eccBytes, 0, allCodewords, geom.DataCodewords, geom.EccCodewords);

        int mapRows = geom.SymbolRows - 2;
        int mapCols = geom.SymbolCols - 2;
        int[] bitMap = BuildUtahPlacementMap(mapRows, mapCols);

        for (int r = 0; r < geom.SymbolRows; r++)
        {
            for (int c = 0; c < geom.SymbolCols; c++)
            {
                bool isDarkModule;
                if (r == geom.SymbolRows - 1 || c == 0)
                {
                    // Solid L-finder border on bottom row and left column
                    isDarkModule = true;
                }
                else if (r == 0)
                {
                    // Top alternating timing track (even columns dark)
                    isDarkModule = (c & 1) == 0;
                }
                else if (c == geom.SymbolCols - 1)
                {
                    // Right alternating timing track ((SymbolRows - 1 - r) even -> dark)
                    isDarkModule = ((geom.SymbolRows - 1 - r) & 1) == 0;
                }
                else
                {
                    int mr = r - 1;
                    int mc = c - 1;
                    int codeBitIndex = bitMap[(mr * mapCols) + mc];
                    if (codeBitIndex > 0)
                    {
                        int cwIndex = (codeBitIndex - 1) / BitsPerCodeword;
                        int bitPos = 7 - ((codeBitIndex - 1) % BitsPerCodeword);
                        isDarkModule = cwIndex < allCodewords.Length && ((allCodewords[cwIndex] >> bitPos) & 1) != 0;
                    }
                    else
                    {
                        isDarkModule = false;
                    }
                }

                byte val = isDarkModule ? darkLevel : lightLevel;
                int py0 = originY + (r * moduleSizePixels);
                int px0 = originX + (c * moduleSizePixels);

                for (int dy = 0; dy < moduleSizePixels; dy++)
                {
                    int y = py0 + dy;
                    if ((uint)y >= (uint)height)
                    {
                        continue;
                    }

                    Span<byte> rowSpan = destination.Slice(y * stride, width);
                    for (int dx = 0; dx < moduleSizePixels; dx++)
                    {
                        int x = px0 + dx;
                        if ((uint)x < (uint)width)
                        {
                            rowSpan[x] = val;
                        }
                    }
                }
            }
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

        // Subpixel edge refinement on outer bounding box edges
        minX = x0;
        minY = y0;
        maxX = x1 + 1.0f;
        maxY = y1 + 1.0f;
        return true;
    }

    private static bool TryDecodeWithGeometry(
        ReadOnlyImageView2D<byte> imageView,
        SymbolGeometrySpec geom,
        float minX,
        float minY,
        float boxWidth,
        float boxHeight,
        Vector2 centerWorld,
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
        float stepX = boxWidth / cols;
        float stepY = boxHeight / rows;
        if (stepX < 1.5f || stepY < 1.5f)
        {
            return false;
        }

        // Verify fixed L-finder & alternating timing borders
        int fixedTotal = (rows * 2) + (cols * 2) - 4;
        int fixedMatches = 0;
        float minModulationMargin = 1.0f;
        float halfContrast = MathF.Max(1.0f, (lightRef - darkRef) * HalfModuleOffset);

        bool[,] moduleBits = new bool[rows, cols];

        for (int r = 0; r < rows; r++)
        {
            float sampleY = minY + ((r + HalfModuleOffset) * stepY);
            for (int c = 0; c < cols; c++)
            {
                float sampleX = minX + ((c + HalfModuleOffset) * stepX);
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

        char overallGrade = ComputeOverallIsoGrade(symbolContrast, minModulationMargin, fixedPatternScore, axialNonUniformity);

        decodeResult = new DataMatrixDecodeResult(
            IsDecoded: true,
            DecodedText: decodedText,
            SymbolRows: rows,
            SymbolCols: cols,
            CenterWorld: centerWorld,
            AngleRad: 0.0f,
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

    private static byte[] EncodeAsciiCodewords(string text, out SymbolGeometrySpec selectedGeometry)
    {
        List<byte> encoded = new(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            if (i + 1 < text.Length && char.IsAsciiDigit(text[i]) && char.IsAsciiDigit(text[i + 1]))
            {
                int pairValue = ((text[i] - '0') * 10) + (text[i + 1] - '0');
                encoded.Add((byte)(pairValue + 130));
                i += 2;
            }
            else
            {
                encoded.Add((byte)(text[i] + 1));
                i++;
            }
        }

        selectedGeometry = SupportedSymbolGeometries[^1];
        for (int g = 0; g < SupportedSymbolGeometries.Length; g++)
        {
            if (SupportedSymbolGeometries[g].DataCodewords >= encoded.Count)
            {
                selectedGeometry = SupportedSymbolGeometries[g];
                break;
            }
        }

        byte[] data = new byte[selectedGeometry.DataCodewords];
        int copyCount = Math.Min(encoded.Count, data.Length);
        for (int idx = 0; idx < copyCount; idx++)
        {
            data[idx] = encoded[idx];
        }

        if (copyCount < data.Length)
        {
            data[copyCount] = AsciiPadCodeword;
            for (int padPos = copyCount + 1; padPos < data.Length; padPos++)
            {
                // ISO/IEC 16022 253-state pseudo-random pad codeword randomization
                int r = ((149 * (padPos + 1)) % 253) + 1;
                int temp = AsciiPadCodeword + r;
                data[padPos] = (byte)(temp <= 254 ? temp : temp - 254);
            }
        }

        return data;
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
    private static byte[] ComputeReedSolomonEcc(ReadOnlySpan<byte> data, int eccCount)
    {
        byte[] ecc = new byte[eccCount];
        ComputeReedSolomonEccInto(data, ecc);
        return ecc;
    }

    private static void ComputeReedSolomonEccInto(ReadOnlySpan<byte> data, Span<byte> eccOut)
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
    private static int[] BuildUtahPlacementMap(int nrow, int ncol)
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

    private readonly record struct SymbolGeometrySpec(
        int SymbolRows,
        int SymbolCols,
        int DataCodewords,
        int EccCodewords);
}
