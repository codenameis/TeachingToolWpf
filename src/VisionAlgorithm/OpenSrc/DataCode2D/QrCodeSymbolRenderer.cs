using System.Text;

namespace VisionAlgorithm.OpenSrc.DataCode2D;

/// <summary>
/// Renders ground-truth ISO/IEC 18004 QR Code 2D symbols (axis-aligned and 4x4 supersampled rotated across -180..+180 degrees)
/// with exact BCH (15, 5) format bits, GF(2^8) (p=0x11D) Reed-Solomon block interleaving, and ISO 4-module quiet zones.
/// </summary>
public static class QrCodeSymbolRenderer
{
    private const byte PadCodewordEven = 0xEC;
    private const byte PadCodewordOdd = 0x11;
    private const float HalfModuleOffset = 0.5f;
    private const float MinRotatedModuleSizePx = 1.5f;
    private const float IsoQuietZoneModules = 4.0f;
    private const float RadiusGuardPaddingPx = 2.0f;
    private const byte DefaultDarkLevel = 24;
    private const byte DefaultLightLevel = 232;
    private const int SubsampleDivisions = 4;

    public static void RenderQrSymbol(
        string payload,
        Span<byte> destination,
        int width,
        int height,
        int stride,
        int originX,
        int originY,
        int moduleSizePixels,
        QrErrorCorrectionLevel errorCorrectionLevel = QrErrorCorrectionLevel.M,
        int maskPattern = 2,
        byte darkLevel = DefaultDarkLevel,
        byte lightLevel = DefaultLightLevel)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentOutOfRangeException.ThrowIfLessThan(moduleSizePixels, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, width);

        bool[,] modules = BuildQrModuleMatrix(payload, errorCorrectionLevel, maskPattern, out int n);
        int quietPx = 4 * moduleSizePixels;

        for (int py = originY - quietPx; py < originY + (n * moduleSizePixels) + quietPx; py++)
        {
            if ((uint)py >= (uint)height)
            {
                continue;
            }

            Span<byte> rowSpan = destination.Slice(py * stride, width);
            for (int px = originX - quietPx; px < originX + (n * moduleSizePixels) + quietPx; px++)
            {
                if ((uint)px < (uint)width)
                {
                    rowSpan[px] = lightLevel;
                }
            }
        }

        for (int r = 0; r < n; r++)
        {
            for (int c = 0; c < n; c++)
            {
                byte val = modules[r, c] ? darkLevel : lightLevel;
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

    public static void RenderRotatedQrSymbol(
        string payload,
        Span<byte> destination,
        int width,
        int height,
        int stride,
        float centerX,
        float centerY,
        float moduleSizePixels,
        float angleRad,
        QrErrorCorrectionLevel errorCorrectionLevel = QrErrorCorrectionLevel.M,
        int maskPattern = 2,
        byte darkLevel = DefaultDarkLevel,
        byte lightLevel = DefaultLightLevel)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentOutOfRangeException.ThrowIfLessThan(moduleSizePixels, MinRotatedModuleSizePx);
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, width);

        bool[,] modules = BuildQrModuleMatrix(payload, errorCorrectionLevel, maskPattern, out int n);
        float symSpan = n * moduleSizePixels;
        float halfSpan = symSpan * HalfModuleOffset;
        float quietMargin = moduleSizePixels * IsoQuietZoneModules;
        float boundHalf = halfSpan + quietMargin;
        float radius = MathF.Sqrt((boundHalf * boundHalf) + (boundHalf * boundHalf)) + RadiusGuardPaddingPx;

        int minPx = Math.Max(0, (int)MathF.Floor(centerX - radius));
        int maxPx = Math.Min(width - 1, (int)MathF.Ceiling(centerX + radius));
        int minPy = Math.Max(0, (int)MathF.Floor(centerY - radius));
        int maxPy = Math.Min(height - 1, (int)MathF.Ceiling(centerY + radius));

        (float sinA, float cosA) = MathF.SinCos(angleRad);
        const float subStep = 1.0f / SubsampleDivisions;
        const float invSamples = 1.0f / (SubsampleDivisions * SubsampleDivisions);

        for (int py = minPy; py <= maxPy; py++)
        {
            Span<byte> rowSpan = destination.Slice(py * stride, width);
            for (int px = minPx; px <= maxPx; px++)
            {
                byte bgVal = rowSpan[px];
                float accum = 0.0f;
                bool anyInsideQuiet = false;

                for (int sy = 0; sy < SubsampleDivisions; sy++)
                {
                    float wy = py + ((sy + HalfModuleOffset) * subStep);
                    float dy = wy - centerY;
                    for (int sx = 0; sx < SubsampleDivisions; sx++)
                    {
                        float wx = px + ((sx + HalfModuleOffset) * subStep);
                        float dx = wx - centerX;

                        float lu = (dx * cosA) + (dy * sinA) + halfSpan;
                        float lv = (-dx * sinA) + (dy * cosA) + halfSpan;

                        if (lu >= 0.0f && lu < symSpan && lv >= 0.0f && lv < symSpan)
                        {
                            int mc = Math.Clamp((int)(lu / moduleSizePixels), 0, n - 1);
                            int mr = Math.Clamp((int)(lv / moduleSizePixels), 0, n - 1);
                            accum += modules[mr, mc] ? darkLevel : lightLevel;
                            anyInsideQuiet = true;
                        }
                        else if (lu >= -quietMargin && lu < (symSpan + quietMargin) &&
                                 lv >= -quietMargin && lv < (symSpan + quietMargin))
                        {
                            accum += lightLevel;
                            anyInsideQuiet = true;
                        }
                        else
                        {
                            accum += bgVal;
                        }
                    }
                }

                if (anyInsideQuiet)
                {
                    rowSpan[px] = (byte)Math.Clamp((int)MathF.Round(accum * invSamples), 0, byte.MaxValue);
                }
            }
        }
    }

    public static bool[,] BuildQrModuleMatrix(
        string payload,
        QrErrorCorrectionLevel errorCorrectionLevel,
        int maskPattern,
        out int dimension)
    {
        ArgumentNullException.ThrowIfNull(payload);
        byte[] utf8 = Encoding.UTF8.GetBytes(payload);
        int clampedMask = maskPattern & 0b111;

        int selectedVersion = SelectSmallestFittingVersion(utf8.Length, errorCorrectionLevel);
        dimension = QrCodeTables.DimensionForVersion(selectedVersion);
        int totalModules = dimension * dimension;

        QrCodeTables.QrBlockLayout layout = QrCodeTables.GetBlockLayout(selectedVersion, errorCorrectionLevel);
        Span<byte> dataCodewords = stackalloc byte[layout.TotalDataCodewords];
        EncodeByteModeDataCodewords(utf8, selectedVersion, dataCodewords);

        Span<byte> interleavedCodewords = stackalloc byte[layout.TotalCodewords];
        InterleaveBlocksWithReedSolomon(dataCodewords, in layout, interleavedCodewords);

        Span<bool> isFunction = stackalloc bool[totalModules];
        Span<bool> expectedDark = stackalloc bool[totalModules];
        QrCodeTables.BuildFunctionMask(selectedVersion, isFunction, expectedDark);

        bool[,] grid = new bool[dimension, dimension];
        for (int r = 0; r < dimension; r++)
        {
            for (int c = 0; c < dimension; c++)
            {
                int idx = (r * dimension) + c;
                if (isFunction[idx])
                {
                    grid[r, c] = expectedDark[idx];
                }
            }
        }

        // Write interleaved data + ECC bits in 2-column upward/downward zig-zag order and apply mask.
        int totalBits = interleavedCodewords.Length * 8;
        int bitIdx = 0;
        bool upward = true;

        for (int rightCol = dimension - 1; rightCol >= 1; rightCol -= 2)
        {
            if (rightCol == 6)
            {
                rightCol = 5;
            }

            for (int step = 0; step < dimension; step++)
            {
                int row = upward ? (dimension - 1 - step) : step;
                for (int dx = 0; dx < 2; dx++)
                {
                    int col = rightCol - dx;
                    if (isFunction[(row * dimension) + col])
                    {
                        continue;
                    }

                    bool dataBit = false;
                    if (bitIdx < totalBits)
                    {
                        dataBit = ((interleavedCodewords[bitIdx >> 3] >> (7 - (bitIdx & 7))) & 1) != 0;
                        bitIdx++;
                    }

                    grid[row, col] = dataBit ^ QrCodeTables.EvaluateMaskBit(clampedMask, row, col);
                }
            }

            upward = !upward;
        }

        // Write both 15-bit Format Information copies
        ushort formatBits = QrCodeTables.EncodeFormatBits(errorCorrectionLevel, clampedMask);
        ReadOnlySpan<int> copy1Cols = [0, 1, 2, 3, 4, 5, 7, 8, 8, 8, 8, 8, 8, 8, 8];
        ReadOnlySpan<int> copy1Rows = [8, 8, 8, 8, 8, 8, 8, 8, 7, 5, 4, 3, 2, 1, 0];
        for (int i = 0; i < 15; i++)
        {
            bool bit = ((formatBits >> (14 - i)) & 1) != 0;
            grid[copy1Rows[i], copy1Cols[i]] = bit;
        }

        for (int i = 0; i < 7; i++)
        {
            bool bit = ((formatBits >> (14 - i)) & 1) != 0;
            grid[dimension - 1 - i, 8] = bit;
        }

        for (int i = 0; i < 8; i++)
        {
            bool bit = ((formatBits >> (7 - i)) & 1) != 0;
            grid[8, dimension - 8 + i] = bit;
        }

        grid[dimension - 8, 8] = true; // Mandatory dark module
        return grid;
    }

    private static int SelectSmallestFittingVersion(int byteLength, QrErrorCorrectionLevel level)
    {
        for (int v = QrCodeTables.MinSupportedVersion; v <= QrCodeTables.MaxSupportedVersion; v++)
        {
            int countBits = QrCodeTables.GetCharacterCountIndicatorBits(0b0100, v);
            int requiredBits = 4 + countBits + (byteLength * 8);
            int capacityBits = QrCodeTables.GetBlockLayout(v, level).TotalDataCodewords * 8;
            if (requiredBits <= capacityBits)
            {
                return v;
            }
        }

        throw new ArgumentOutOfRangeException(
            nameof(byteLength),
            $"Payload of {byteLength} UTF-8 bytes exceeds QR Version {QrCodeTables.MaxSupportedVersion} capacity at ECC level {level}.");
    }

    private static void EncodeByteModeDataCodewords(ReadOnlySpan<byte> utf8, int version, Span<byte> dataOut)
    {
        dataOut.Clear();
        int bitPos = 0;
        int capacityBits = dataOut.Length * 8;

        WriteBits(dataOut, ref bitPos, 0b0100, 4);
        int countBits = QrCodeTables.GetCharacterCountIndicatorBits(0b0100, version);
        WriteBits(dataOut, ref bitPos, utf8.Length, countBits);

        for (int i = 0; i < utf8.Length; i++)
        {
            WriteBits(dataOut, ref bitPos, utf8[i], 8);
        }

        int terminatorBits = Math.Min(4, capacityBits - bitPos);
        WriteBits(dataOut, ref bitPos, 0, terminatorBits);

        if ((bitPos & 7) != 0)
        {
            bitPos = (bitPos + 7) & ~7;
        }

        int padIdx = bitPos >> 3;
        bool evenPad = true;
        while (padIdx < dataOut.Length)
        {
            dataOut[padIdx++] = evenPad ? PadCodewordEven : PadCodewordOdd;
            evenPad = !evenPad;
        }
    }

    private static void WriteBits(Span<byte> dst, ref int bitPos, int value, int bitCount)
    {
        for (int i = bitCount - 1; i >= 0; i--)
        {
            if (((value >> i) & 1) != 0)
            {
                dst[bitPos >> 3] |= (byte)(1 << (7 - (bitPos & 7)));
            }

            bitPos++;
        }
    }

    private static void InterleaveBlocksWithReedSolomon(
        ReadOnlySpan<byte> dataCodewords,
        in QrCodeTables.QrBlockLayout layout,
        Span<byte> interleavedOut)
    {
        int totalBlocks = layout.TotalBlocks;
        int maxData = Math.Max(layout.Group1DataCodewords, layout.Group2DataCodewords);
        int eccLen = layout.EccCodewordsPerBlock;
        int stride = maxData + eccLen;

        Span<byte> blocks = stackalloc byte[totalBlocks * stride];
        Span<int> dataLens = stackalloc int[totalBlocks];

        int srcOffset = 0;
        for (int b = 0; b < totalBlocks; b++)
        {
            int dLen = b < layout.Group1Blocks ? layout.Group1DataCodewords : layout.Group2DataCodewords;
            dataLens[b] = dLen;

            ReadOnlySpan<byte> blockData = dataCodewords.Slice(srcOffset, dLen);
            srcOffset += dLen;

            Span<byte> blockDst = blocks.Slice(b * stride, dLen + eccLen);
            blockData.CopyTo(blockDst.Slice(0, dLen));
            QrCodeTables.ComputeReedSolomonEcc(blockData, blockDst.Slice(dLen, eccLen));
        }

        int outIdx = 0;
        for (int col = 0; col < maxData; col++)
        {
            for (int b = 0; b < totalBlocks; b++)
            {
                if (col < dataLens[b])
                {
                    interleavedOut[outIdx++] = blocks[(b * stride) + col];
                }
            }
        }

        for (int col = 0; col < eccLen; col++)
        {
            for (int b = 0; b < totalBlocks; b++)
            {
                int dLen = dataLens[b];
                interleavedOut[outIdx++] = blocks[(b * stride) + dLen + col];
            }
        }
    }
}
