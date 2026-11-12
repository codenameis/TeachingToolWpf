namespace VisionAlgorithm.OpenSrc.DataCode2D;

/// <summary>
/// Renders ground-truth ISO/IEC 16022 ECC 200 DataMatrix 2D symbols (axis-aligned and 4x4 supersampled rotated)
/// using Utah L-shaped module placement and GF(2^8) Reed-Solomon check codewords.
/// </summary>
public static class Ecc200SymbolRenderer
{
    private const byte AsciiPadCodeword = 129;
    private const byte DigitPairBaseCodeword = 130;
    private const int BitsPerCodeword = 8;
    private const int PadPrngMultiplier = 149;
    private const int PadPrngModulus = 253;
    private const int PadCodewordWrapModulus = 254;
    private const float HalfModuleOffset = 0.5f;
    private const float MinRotatedModuleSizePx = 1.5f;
    private const float QuietMarginModules = 1.5f;
    private const float RadiusGuardPaddingPx = 2.0f;
    private const byte DefaultDarkLevel = 25;
    private const byte DefaultLightLevel = 230;
    private const int MaxByteIntensity = byte.MaxValue;

    /// <summary>
    /// Renders a ground-truth ISO/IEC 16022 ECC 200 DataMatrix symbol for <paramref name="asciiPayload"/> onto a strided
    /// 8-bit image buffer using the exact Utah L-shaped module placement and $\text{GF}(2^8)$ Reed-Solomon check codewords.
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
        byte darkLevel = DefaultDarkLevel,
        byte lightLevel = DefaultLightLevel)
    {
        ArgumentNullException.ThrowIfNull(asciiPayload);
        ArgumentOutOfRangeException.ThrowIfLessThan(moduleSizePixels, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, width);

        bool[,] moduleGrid = BuildEcc200ModuleMatrix(asciiPayload, out DataMatrixEcc200Decoder.SymbolGeometrySpec geom);

        for (int r = 0; r < geom.SymbolRows; r++)
        {
            for (int c = 0; c < geom.SymbolCols; c++)
            {
                byte val = moduleGrid[r, c] ? darkLevel : lightLevel;
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

    /// <summary>
    /// Renders a 4x4 supersampled anti-aliased ISO/IEC 16022 ECC 200 DataMatrix symbol rotated by <paramref name="angleRad"/>
    /// around <c>(centerX, centerY)</c> in world coordinates.
    /// </summary>
    public static void RenderRotatedEcc200Symbol(
        string asciiPayload,
        Span<byte> destination,
        int width,
        int height,
        int stride,
        float centerX,
        float centerY,
        float moduleSizePixels,
        float angleRad,
        byte darkLevel = DefaultDarkLevel,
        byte lightLevel = DefaultLightLevel)
    {
        ArgumentNullException.ThrowIfNull(asciiPayload);
        ArgumentOutOfRangeException.ThrowIfLessThan(moduleSizePixels, MinRotatedModuleSizePx);
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, width);

        bool[,] moduleGrid = BuildEcc200ModuleMatrix(asciiPayload, out DataMatrixEcc200Decoder.SymbolGeometrySpec geom);
        float symW = geom.SymbolCols * moduleSizePixels;
        float symH = geom.SymbolRows * moduleSizePixels;
        float halfW = symW * HalfModuleOffset;
        float halfH = symH * HalfModuleOffset;
        float quietMargin = moduleSizePixels * QuietMarginModules;
        float radius = MathF.Sqrt(((halfW + quietMargin) * (halfW + quietMargin)) + ((halfH + quietMargin) * (halfH + quietMargin))) + RadiusGuardPaddingPx;

        int minPx = Math.Max(0, (int)MathF.Floor(centerX - radius));
        int maxPx = Math.Min(width - 1, (int)MathF.Ceiling(centerX + radius));
        int minPy = Math.Max(0, (int)MathF.Floor(centerY - radius));
        int maxPy = Math.Min(height - 1, (int)MathF.Ceiling(centerY + radius));

        (float sinA, float cosA) = MathF.SinCos(angleRad);
        const int subDiv = 4;
        const float subStep = 1.0f / subDiv;
        const float invSamples = 1.0f / (subDiv * subDiv);

        for (int py = minPy; py <= maxPy; py++)
        {
            Span<byte> rowSpan = destination.Slice(py * stride, width);
            for (int px = minPx; px <= maxPx; px++)
            {
                byte bgVal = rowSpan[px];
                float accum = 0.0f;
                bool anyInsideQuiet = false;

                for (int sy = 0; sy < subDiv; sy++)
                {
                    float wy = py + ((sy + HalfModuleOffset) * subStep);
                    float dy = wy - centerY;
                    for (int sx = 0; sx < subDiv; sx++)
                    {
                        float wx = px + ((sx + HalfModuleOffset) * subStep);
                        float dx = wx - centerX;

                        float lu = (dx * cosA) + (dy * sinA) + halfW;
                        float lv = (-dx * sinA) + (dy * cosA) + halfH;

                        if (lu >= 0.0f && lu < symW && lv >= 0.0f && lv < symH)
                        {
                            anyInsideQuiet = true;
                            int col = Math.Clamp((int)(lu / moduleSizePixels), 0, geom.SymbolCols - 1);
                            int row = Math.Clamp((int)(lv / moduleSizePixels), 0, geom.SymbolRows - 1);
                            accum += moduleGrid[row, col] ? darkLevel : lightLevel;
                        }
                        else if (lu >= -quietMargin && lu <= symW + quietMargin && lv >= -quietMargin && lv <= symH + quietMargin)
                        {
                            anyInsideQuiet = true;
                            accum += lightLevel;
                        }
                        else
                        {
                            accum += bgVal;
                        }
                    }
                }

                if (anyInsideQuiet)
                {
                    rowSpan[px] = (byte)Math.Clamp((int)MathF.Round(accum * invSamples), 0, MaxByteIntensity);
                }
            }
        }
    }

    private static bool[,] BuildEcc200ModuleMatrix(string asciiPayload, out DataMatrixEcc200Decoder.SymbolGeometrySpec geom)
    {
        byte[] dataBytes = EncodeAsciiCodewords(asciiPayload, out geom);
        byte[] eccBytes = ComputeReedSolomonEcc(dataBytes, geom.EccCodewords);

        byte[] allCodewords = new byte[geom.DataCodewords + geom.EccCodewords];
        Array.Copy(dataBytes, 0, allCodewords, 0, geom.DataCodewords);
        Array.Copy(eccBytes, 0, allCodewords, geom.DataCodewords, geom.EccCodewords);

        int mapRows = geom.SymbolRows - 2;
        int mapCols = geom.SymbolCols - 2;
        int[] bitMap = DataMatrixEcc200Decoder.BuildUtahPlacementMap(mapRows, mapCols);
        bool[,] grid = new bool[geom.SymbolRows, geom.SymbolCols];

        for (int r = 0; r < geom.SymbolRows; r++)
        {
            for (int c = 0; c < geom.SymbolCols; c++)
            {
                bool isDarkModule;
                if (r == geom.SymbolRows - 1 || c == 0)
                {
                    isDarkModule = true;
                }
                else if (r == 0)
                {
                    isDarkModule = (c & 1) == 0;
                }
                else if (c == geom.SymbolCols - 1)
                {
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

                grid[r, c] = isDarkModule;
            }
        }

        return grid;
    }

    private static byte[] EncodeAsciiCodewords(string text, out DataMatrixEcc200Decoder.SymbolGeometrySpec selectedGeometry)
    {
        List<byte> encoded = new(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            if (i + 1 < text.Length && char.IsAsciiDigit(text[i]) && char.IsAsciiDigit(text[i + 1]))
            {
                int pairValue = ((text[i] - '0') * 10) + (text[i + 1] - '0');
                encoded.Add((byte)(pairValue + DigitPairBaseCodeword));
                i += 2;
            }
            else
            {
                encoded.Add((byte)(text[i] + 1));
                i++;
            }
        }

        var geometries = DataMatrixEcc200Decoder.SupportedSymbolGeometries;
        selectedGeometry = geometries[^1];
        for (int g = 0; g < geometries.Length; g++)
        {
            if (geometries[g].DataCodewords >= encoded.Count)
            {
                selectedGeometry = geometries[g];
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
                // [Math Why]: ISO/IEC 16022 253-state pseudo-random pad codeword randomization.
                int r = ((PadPrngMultiplier * (padPos + 1)) % PadPrngModulus) + 1;
                int temp = AsciiPadCodeword + r;
                data[padPos] = (byte)(temp <= PadCodewordWrapModulus ? temp : temp - PadCodewordWrapModulus);
            }
        }

        return data;
    }

    private static byte[] ComputeReedSolomonEcc(ReadOnlySpan<byte> data, int eccCount)
    {
        byte[] ecc = new byte[eccCount];
        DataMatrixEcc200Decoder.ComputeReedSolomonEccInto(data, ecc);
        return ecc;
    }
}
