using System.Numerics;
using System.Runtime.CompilerServices;

namespace VisionAlgorithm.OpenSrc.DataCode2D;

/// <summary>
/// Internal ISO/IEC 18004 lookup tables, Galois Field GF(2^8) (p(x) = x^8 + x^4 + x^3 + x^2 + 1 = 0x11D)
/// Reed-Solomon codec, BCH (15, 5) / Golay (18, 6) format &amp; version codecs, function-module masks,
/// and 3x3 projective homography utilities.
/// </summary>
internal static class QrCodeTables
{
    public const int MinSupportedVersion = 1;
    public const int MaxSupportedVersion = 10;

    // [Math Why]: ISO/IEC 18004 QR Code Galois Field GF(2^8) primitive polynomial x^8 + x^4 + x^3 + x^2 + 1 = 0x11D (285).
    private const int GfPrimitivePolynomial = 0x11D;
    private const int GfFieldSize = 256;
    private const int GfMaxElementIndex = 255;

    // [Math Why]: Format information BCH (15, 5) generator x^10 + x^8 + x^5 + x^4 + x^2 + x + 1 = 0x537, XOR mask = 0x5412, d_min = 7 (corrects <= 3 bit errors).
    private const int FormatGeneratorPolynomial = 0x537;
    private const int FormatXorMask = 0x5412;
    private const int FormatDataBitCount = 5;
    private const int FormatParityBitCount = 10;
    private const int FormatCodewordCount = 32;
    public const int MaxFormatBitErrors = 3;

    // [Math Why]: Version information Golay (18, 6) generator x^12 + x^11 + x^10 + x^9 + x^8 + x^5 + x^2 + 1 = 0x1F25, d_min = 8 (corrects <= 3 bit errors).
    private const int VersionGeneratorPolynomial = 0x1F25;
    private const int VersionParityBitCount = 12;
    public const int MinVersionWithVersionInfo = 7;
    public const int MaxVersionBitErrors = 3;

    public const int AlphanumericAlphabetCount = 45;

    private static readonly byte[] GfExp = new byte[GfFieldSize * 2];
    private static readonly byte[] GfLog = new byte[GfFieldSize];
    private static readonly ushort[] ValidFormatCodewords = new ushort[FormatCodewordCount];

    private static readonly int[][] AlignmentCentersByVersion =
    [
        [],
        [],
        [6, 18],
        [6, 22],
        [6, 26],
        [6, 30],
        [6, 34],
        [6, 22, 38],
        [6, 24, 42],
        [6, 26, 46],
        [6, 28, 50]
    ];

    // Indexed by [(version - 1) * 4 + (int)ecLevel] where ecLevel: L=0, M=1, Q=2, H=3.
    private static readonly QrBlockLayout[] BlockLayouts =
    [
        // Version 1 (21x21, 26 total codewords)
        new(7, 1, 19, 0, 0),
        new(10, 1, 16, 0, 0),
        new(13, 1, 13, 0, 0),
        new(17, 1, 9, 0, 0),

        // Version 2 (25x25, 44 total codewords)
        new(10, 1, 34, 0, 0),
        new(16, 1, 28, 0, 0),
        new(22, 1, 22, 0, 0),
        new(28, 1, 16, 0, 0),

        // Version 3 (29x29, 70 total codewords)
        new(15, 1, 55, 0, 0),
        new(26, 1, 44, 0, 0),
        new(18, 2, 17, 0, 0),
        new(22, 2, 13, 0, 0),

        // Version 4 (33x33, 100 total codewords)
        new(20, 1, 80, 0, 0),
        new(18, 2, 32, 0, 0),
        new(26, 2, 24, 0, 0),
        new(16, 4, 9, 0, 0),

        // Version 5 (37x37, 134 total codewords)
        new(26, 1, 108, 0, 0),
        new(24, 2, 43, 0, 0),
        new(18, 2, 15, 2, 16),
        new(22, 2, 11, 2, 12),

        // Version 6 (41x41, 172 total codewords)
        new(18, 2, 68, 0, 0),
        new(16, 4, 27, 0, 0),
        new(24, 4, 19, 0, 0),
        new(28, 4, 15, 0, 0),

        // Version 7 (45x45, 196 total codewords)
        new(20, 2, 78, 0, 0),
        new(18, 4, 31, 0, 0),
        new(18, 2, 14, 4, 15),
        new(26, 4, 13, 1, 14),

        // Version 8 (49x49, 242 total codewords)
        new(24, 2, 97, 0, 0),
        new(22, 2, 38, 2, 39),
        new(22, 4, 18, 2, 19),
        new(26, 4, 14, 2, 15),

        // Version 9 (53x53, 292 total codewords)
        new(30, 2, 116, 0, 0),
        new(22, 3, 36, 2, 37),
        new(20, 4, 16, 4, 17),
        new(24, 4, 12, 4, 13),

        // Version 10 (57x57, 346 total codewords)
        new(18, 2, 68, 2, 69),
        new(26, 4, 43, 1, 44),
        new(24, 6, 19, 2, 20),
        new(28, 6, 15, 2, 16)
    ];

    static QrCodeTables()
    {
        int value = 1;
        for (int i = 0; i < GfMaxElementIndex; i++)
        {
            GfExp[i] = (byte)value;
            GfExp[i + GfMaxElementIndex] = (byte)value;
            GfLog[value] = (byte)i;

            value <<= 1;
            if (value >= GfFieldSize)
            {
                value ^= GfPrimitivePolynomial;
            }
        }

        GfLog[0] = 0;

        for (int data5 = 0; data5 < FormatCodewordCount; data5++)
        {
            ValidFormatCodewords[data5] = EncodeFormatBitsRaw(data5);
        }
    }

    internal readonly record struct QrBlockLayout(
        int EccCodewordsPerBlock,
        int Group1Blocks,
        int Group1DataCodewords,
        int Group2Blocks,
        int Group2DataCodewords)
    {
        public int TotalBlocks => Group1Blocks + Group2Blocks;

        public int TotalDataCodewords => (Group1Blocks * Group1DataCodewords) + (Group2Blocks * Group2DataCodewords);

        public int TotalCodewords => TotalDataCodewords + (TotalBlocks * EccCodewordsPerBlock);
    }

    internal readonly struct Homography3x3(
        double h00, double h01, double h02,
        double h10, double h11, double h12,
        double h20, double h21, double h22)
    {
        public readonly double H00 = h00;
        public readonly double H01 = h01;
        public readonly double H02 = h02;
        public readonly double H10 = h10;
        public readonly double H11 = h11;
        public readonly double H12 = h12;
        public readonly double H20 = h20;
        public readonly double H21 = h21;
        public readonly double H22 = h22;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vector2 Project(double u, double v)
        {
            double w = (H20 * u) + (H21 * v) + H22;
            double invW = Math.Abs(w) > 1e-12 ? 1.0 / w : 1.0;
            return new Vector2(
                (float)(((H00 * u) + (H01 * v) + H02) * invW),
                (float)(((H10 * u) + (H11 * v) + H12) * invW));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int DimensionForVersion(int version) => (4 * version) + 17;

    /// <summary>
    /// Maps an ISO/IEC 18004 Table 5 Alphanumeric value (<c>0..44</c>) to its ASCII character without embedding string literals.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryMapAlphanumericChar(int code, out char ch)
    {
        if ((uint)code < 10u)
        {
            ch = (char)('0' + code);
            return true;
        }

        if ((uint)code < 36u)
        {
            ch = (char)('A' + (code - 10));
            return true;
        }

        switch (code)
        {
            case 36: ch = ' '; return true;
            case 37: ch = '$'; return true;
            case 38: ch = '%'; return true;
            case 39: ch = '*'; return true;
            case 40: ch = '+'; return true;
            case 41: ch = '-'; return true;
            case 42: ch = '.'; return true;
            case 43: ch = '/'; return true;
            case 44: ch = ':'; return true;
            default:
                ch = '\0';
                return false;
        }
    }

    public static ReadOnlySpan<int> GetAlignmentCenters(int version)
        => (uint)version <= (uint)MaxSupportedVersion ? AlignmentCentersByVersion[version] : ReadOnlySpan<int>.Empty;

    public static QrBlockLayout GetBlockLayout(int version, QrErrorCorrectionLevel level)
        => BlockLayouts[((version - 1) * 4) + (int)level];

    public static int ToFormatIndicatorBits(QrErrorCorrectionLevel level) => level switch
    {
        QrErrorCorrectionLevel.L => 0b01,
        QrErrorCorrectionLevel.M => 0b00,
        QrErrorCorrectionLevel.Q => 0b11,
        QrErrorCorrectionLevel.H => 0b10,
        _ => 0b00
    };

    public static QrErrorCorrectionLevel FromFormatIndicatorBits(int indicator2Bits) => (indicator2Bits & 0b11) switch
    {
        0b01 => QrErrorCorrectionLevel.L,
        0b00 => QrErrorCorrectionLevel.M,
        0b11 => QrErrorCorrectionLevel.Q,
        0b10 => QrErrorCorrectionLevel.H,
        _ => QrErrorCorrectionLevel.M
    };

    public static ushort EncodeFormatBits(QrErrorCorrectionLevel level, int maskPattern)
    {
        int data5 = (ToFormatIndicatorBits(level) << 3) | (maskPattern & 0b111);
        return ValidFormatCodewords[data5];
    }

    public static bool TryDecodeFormatBits(
        ushort copy1Bits,
        ushort copy2Bits,
        out QrErrorCorrectionLevel level,
        out int maskPattern,
        out int bestBitDistance)
    {
        int bestData5 = -1;
        bestBitDistance = int.MaxValue;

        for (int data5 = 0; data5 < FormatCodewordCount; data5++)
        {
            ushort candidate = ValidFormatCodewords[data5];
            int dist1 = BitOperations.PopCount((uint)(copy1Bits ^ candidate));
            int dist2 = BitOperations.PopCount((uint)(copy2Bits ^ candidate));
            int minDist = dist1 < dist2 ? dist1 : dist2;

            if (minDist < bestBitDistance)
            {
                bestBitDistance = minDist;
                bestData5 = data5;
            }
        }

        if (bestData5 >= 0 && bestBitDistance <= MaxFormatBitErrors)
        {
            level = FromFormatIndicatorBits((bestData5 >> 3) & 0b11);
            maskPattern = bestData5 & 0b111;
            return true;
        }

        level = QrErrorCorrectionLevel.M;
        maskPattern = 0;
        return false;
    }

    public static int EncodeVersionBits(int version)
    {
        int rem = version << VersionParityBitCount;
        for (int bit = 17; bit >= VersionParityBitCount; bit--)
        {
            if (((rem >> bit) & 1) != 0)
            {
                rem ^= VersionGeneratorPolynomial << (bit - VersionParityBitCount);
            }
        }

        return (version << VersionParityBitCount) | rem;
    }

    public static bool TryDecodeVersionBits(int bits1, int bits2, out int decodedVersion)
    {
        int bestVersion = -1;
        int bestDist = int.MaxValue;

        for (int v = MinVersionWithVersionInfo; v <= MaxSupportedVersion; v++)
        {
            int target = EncodeVersionBits(v);
            int d1 = BitOperations.PopCount((uint)((bits1 ^ target) & 0x3FFFF));
            int d2 = BitOperations.PopCount((uint)((bits2 ^ target) & 0x3FFFF));
            int minD = d1 < d2 ? d1 : d2;
            if (minD < bestDist)
            {
                bestDist = minD;
                bestVersion = v;
            }
        }

        if (bestVersion >= MinVersionWithVersionInfo && bestDist <= MaxVersionBitErrors)
        {
            decodedVersion = bestVersion;
            return true;
        }

        decodedVersion = 0;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool EvaluateMaskBit(int maskPattern, int row, int col) => maskPattern switch
    {
        0 => ((row + col) & 1) == 0,
        1 => (row & 1) == 0,
        2 => (col % 3) == 0,
        3 => ((row + col) % 3) == 0,
        4 => (((row / 2) + (col / 3)) & 1) == 0,
        5 => (((row * col) & 1) + ((row * col) % 3)) == 0,
        6 => ((((row * col) & 1) + ((row * col) % 3)) & 1) == 0,
        7 => ((((row + col) & 1) + ((row * col) % 3)) & 1) == 0,
        _ => false
    };

    /// <summary>
    /// Populates the function-pattern mask and expected dark/light polarity for all fixed patterns of <paramref name="version"/>.
    /// </summary>
    public static void BuildFunctionMask(int version, Span<bool> isFunction, Span<bool> expectedDark)
    {
        int n = DimensionForVersion(version);
        isFunction.Slice(0, n * n).Clear();
        expectedDark.Slice(0, n * n).Clear();

        PlaceFinderWithSeparator(n, topRow: 0, leftCol: 0, isFunction, expectedDark);
        PlaceFinderWithSeparator(n, topRow: 0, leftCol: n - 7, isFunction, expectedDark);
        PlaceFinderWithSeparator(n, topRow: n - 7, leftCol: 0, isFunction, expectedDark);

        // Timing patterns along row 6 and col 6
        for (int i = 8; i < n - 8; i++)
        {
            bool dark = (i & 1) == 0;
            isFunction[(6 * n) + i] = true;
            expectedDark[(6 * n) + i] = dark;
            isFunction[(i * n) + 6] = true;
            expectedDark[(i * n) + 6] = dark;
        }

        // Alignment patterns (5x5)
        ReadOnlySpan<int> centers = GetAlignmentCenters(version);
        for (int iy = 0; iy < centers.Length; iy++)
        {
            for (int ix = 0; ix < centers.Length; ix++)
            {
                if ((ix == 0 && iy == 0) ||
                    (ix == centers.Length - 1 && iy == 0) ||
                    (ix == 0 && iy == centers.Length - 1))
                {
                    continue;
                }

                int cr = centers[iy];
                int cc = centers[ix];
                for (int dr = -2; dr <= 2; dr++)
                {
                    for (int dc = -2; dc <= 2; dc++)
                    {
                        int r = cr + dr;
                        int c = cc + dc;
                        int chebyshev = Math.Max(Math.Abs(dr), Math.Abs(dc));
                        isFunction[(r * n) + c] = true;
                        expectedDark[(r * n) + c] = chebyshev != 1;
                    }
                }
            }
        }

        // Format information reservation (Copy 1 + Copy 2 + mandatory Dark Module at (4V+9, 8) = (n-8, 8))
        for (int i = 0; i <= 8; i++)
        {
            if (i != 6)
            {
                isFunction[(8 * n) + i] = true;
                isFunction[(i * n) + 8] = true;
            }
        }

        for (int i = 0; i < 8; i++)
        {
            isFunction[(8 * n) + (n - 1 - i)] = true;
            isFunction[((n - 1 - i) * n) + 8] = true;
        }

        isFunction[((n - 8) * n) + 8] = true;
        expectedDark[((n - 8) * n) + 8] = true;

        // Version information reservation for V >= 7
        if (version >= MinVersionWithVersionInfo)
        {
            int verBits = EncodeVersionBits(version);
            for (int i = 0; i < 18; i++)
            {
                bool bit = ((verBits >> i) & 1) != 0;
                int r1 = (n - 11) + (i % 3);
                int c1 = i / 3;
                isFunction[(r1 * n) + c1] = true;
                expectedDark[(r1 * n) + c1] = bit;

                int r2 = i / 3;
                int c2 = (n - 11) + (i % 3);
                isFunction[(r2 * n) + c2] = true;
                expectedDark[(r2 * n) + c2] = bit;
            }
        }
    }

    public static void ComputeReedSolomonEcc(ReadOnlySpan<byte> data, Span<byte> eccOut)
    {
        int eccLen = eccOut.Length;
        eccOut.Clear();

        // Generator polynomial g(x) = prod_{j=0}^{eccLen-1} (x - alpha^j), stored from degree eccLen-1..0 (monic x^eccLen implicit).
        Span<byte> gen = stackalloc byte[eccLen];
        gen[eccLen - 1] = 1;
        int rootPow = 0;
        for (int i = 0; i < eccLen; i++)
        {
            byte root = GfExp[rootPow];
            for (int j = 0; j < eccLen - 1; j++)
            {
                gen[j] = (byte)(GfMultiply(gen[j], root) ^ gen[j + 1]);
            }

            gen[eccLen - 1] = GfMultiply(gen[eccLen - 1], root);
            rootPow++;
        }

        for (int i = 0; i < data.Length; i++)
        {
            byte factor = (byte)(data[i] ^ eccOut[0]);
            for (int j = 0; j < eccLen - 1; j++)
            {
                eccOut[j] = (byte)(eccOut[j + 1] ^ GfMultiply(gen[j], factor));
            }

            eccOut[eccLen - 1] = GfMultiply(gen[eccLen - 1], factor);
        }
    }

    /// <summary>
    /// Verifies and corrects up to <c>eccCount / 2</c> byte errors in-place in <paramref name="block"/> using
    /// Berlekamp-Massey + Chien Search + Forney (FCR = 0, primitive polynomial 0x11D).
    /// </summary>
    public static bool TryCorrectReedSolomonBlock(Span<byte> block, int eccCount, out int correctedErrors)
    {
        correctedErrors = 0;
        int n = block.Length;
        if (eccCount <= 0 || eccCount >= n)
        {
            return false;
        }

        Span<byte> syndromes = stackalloc byte[eccCount];
        bool hasNonZeroSyndrome = false;

        for (int j = 0; j < eccCount; j++)
        {
            byte alphaJ = GfExp[j];
            byte eval = 0;
            for (int i = 0; i < n; i++)
            {
                eval = (byte)(GfMultiply(eval, alphaJ) ^ block[i]);
            }

            syndromes[j] = eval;
            hasNonZeroSyndrome |= eval != 0;
        }

        if (!hasNonZeroSyndrome)
        {
            return true;
        }

        int maxErrors = eccCount / 2;
        Span<byte> sigma = stackalloc byte[eccCount + 1];
        Span<byte> prevSigma = stackalloc byte[eccCount + 1];
        Span<byte> nextSigma = stackalloc byte[eccCount + 1];
        sigma[0] = 1;
        prevSigma[0] = 1;

        int locatorDegree = 0;
        int shiftSteps = 1;
        byte prevDiscrepancy = 1;

        for (int step = 0; step < eccCount; step++)
        {
            byte discrepancy = syndromes[step];
            for (int i = 1; i <= locatorDegree; i++)
            {
                discrepancy ^= GfMultiply(sigma[i], syndromes[step - i]);
            }

            if (discrepancy == 0)
            {
                shiftSteps++;
                continue;
            }

            sigma.CopyTo(nextSigma);
            byte scale = GfDivide(discrepancy, prevDiscrepancy);
            for (int i = 0; i + shiftSteps <= eccCount; i++)
            {
                if (prevSigma[i] != 0)
                {
                    nextSigma[i + shiftSteps] ^= GfMultiply(scale, prevSigma[i]);
                }
            }

            if ((2 * locatorDegree) <= step)
            {
                locatorDegree = step + 1 - locatorDegree;
                sigma.CopyTo(prevSigma);
                prevDiscrepancy = discrepancy;
                shiftSteps = 1;
            }
            else
            {
                shiftSteps++;
            }

            nextSigma.CopyTo(sigma);
        }

        if (locatorDegree <= 0 || locatorDegree > maxErrors)
        {
            return false;
        }

        // Chien search: find roots z = X_k^{-1} of sigma(z) = 0 where X_k = alpha^{power}, array index = n - 1 - power.
        Span<int> errorIndices = stackalloc int[locatorDegree];
        Span<int> errorPowers = stackalloc int[locatorDegree];
        int foundRoots = 0;

        for (int power = 0; power < n; power++)
        {
            byte z = power == 0 ? (byte)1 : GfExp[GfMaxElementIndex - power];
            byte sum = 1;
            byte zPow = 1;
            for (int i = 1; i <= locatorDegree; i++)
            {
                zPow = GfMultiply(zPow, z);
                sum ^= GfMultiply(sigma[i], zPow);
            }

            if (sum == 0)
            {
                if (foundRoots >= locatorDegree)
                {
                    return false;
                }

                errorIndices[foundRoots] = (n - 1) - power;
                errorPowers[foundRoots] = power;
                foundRoots++;
            }
        }

        if (foundRoots != locatorDegree)
        {
            return false;
        }

        // Error evaluator polynomial Omega(z) = S(z) * sigma(z) mod z^{eccCount}
        Span<byte> omega = stackalloc byte[eccCount];
        for (int i = 0; i < eccCount; i++)
        {
            byte acc = 0;
            int maxJ = Math.Min(i, locatorDegree);
            for (int j = 0; j <= maxJ; j++)
            {
                acc ^= GfMultiply(sigma[j], syndromes[i - j]);
            }

            omega[i] = acc;
        }

        // [Math Why]: For FCR = 0 (roots alpha^0 .. alpha^{k-1}), Forney's formula requires X_k^{1-FCR} = X_k factor:
        // e_k = X_k * Omega(X_k^{-1}) / sigma'(X_k^{-1}).
        for (int r = 0; r < foundRoots; r++)
        {
            int power = errorPowers[r];
            byte xk = GfExp[power];
            byte xkInv = power == 0 ? (byte)1 : GfExp[GfMaxElementIndex - power];

            byte omegaVal = 0;
            byte zPow = 1;
            for (int i = 0; i < eccCount; i++)
            {
                omegaVal ^= GfMultiply(omega[i], zPow);
                zPow = GfMultiply(zPow, xkInv);
            }

            byte sigmaDeriv = 0;
            byte zEvenPow = 1;
            for (int i = 1; i <= locatorDegree; i += 2)
            {
                sigmaDeriv ^= GfMultiply(sigma[i], zEvenPow);
                zEvenPow = GfMultiply(zEvenPow, GfMultiply(xkInv, xkInv));
            }

            if (sigmaDeriv == 0)
            {
                return false;
            }

            byte errorMagnitude = GfMultiply(xk, GfDivide(omegaVal, sigmaDeriv));
            block[errorIndices[r]] ^= errorMagnitude;
        }

        // Re-verify all syndromes are strictly zero after correction
        for (int j = 0; j < eccCount; j++)
        {
            byte alphaJ = GfExp[j];
            byte eval = 0;
            for (int i = 0; i < n; i++)
            {
                eval = (byte)(GfMultiply(eval, alphaJ) ^ block[i]);
            }

            if (eval != 0)
            {
                return false;
            }
        }

        correctedErrors = foundRoots;
        return true;
    }

    /// <summary>
    /// Solves the exact 8-DOF planar projective homography mapping 4 module-space points <c>(u_i, v_i)</c>
    /// to 4 world-space points <c>(x_i, y_i)</c> via Gaussian elimination with partial pivoting.
    /// </summary>
    public static bool TrySolveHomography(
        ReadOnlySpan<Vector2> srcModulePoints4,
        ReadOnlySpan<Vector2> dstWorldPoints4,
        out Homography3x3 homography)
    {
        Span<double> mat = stackalloc double[8 * 9];
        for (int i = 0; i < 4; i++)
        {
            double u = srcModulePoints4[i].X;
            double v = srcModulePoints4[i].Y;
            double x = dstWorldPoints4[i].X;
            double y = dstWorldPoints4[i].Y;

            int r0 = (2 * i) * 9;
            mat[r0 + 0] = u;
            mat[r0 + 1] = v;
            mat[r0 + 2] = 1.0;
            mat[r0 + 3] = 0.0;
            mat[r0 + 4] = 0.0;
            mat[r0 + 5] = 0.0;
            mat[r0 + 6] = -u * x;
            mat[r0 + 7] = -v * x;
            mat[r0 + 8] = x;

            int r1 = ((2 * i) + 1) * 9;
            mat[r1 + 0] = 0.0;
            mat[r1 + 1] = 0.0;
            mat[r1 + 2] = 0.0;
            mat[r1 + 3] = u;
            mat[r1 + 4] = v;
            mat[r1 + 5] = 1.0;
            mat[r1 + 6] = -u * y;
            mat[r1 + 7] = -v * y;
            mat[r1 + 8] = y;
        }

        for (int col = 0; col < 8; col++)
        {
            int pivotRow = col;
            double maxAbs = Math.Abs(mat[(col * 9) + col]);
            for (int r = col + 1; r < 8; r++)
            {
                double cand = Math.Abs(mat[(r * 9) + col]);
                if (cand > maxAbs)
                {
                    maxAbs = cand;
                    pivotRow = r;
                }
            }

            if (maxAbs < 1e-11)
            {
                homography = default;
                return false;
            }

            if (pivotRow != col)
            {
                for (int c = col; c < 9; c++)
                {
                    (mat[(col * 9) + c], mat[(pivotRow * 9) + c]) = (mat[(pivotRow * 9) + c], mat[(col * 9) + c]);
                }
            }

            double invPivot = 1.0 / mat[(col * 9) + col];
            for (int c = col; c < 9; c++)
            {
                mat[(col * 9) + c] *= invPivot;
            }

            for (int r = 0; r < 8; r++)
            {
                if (r == col)
                {
                    continue;
                }

                double factor = mat[(r * 9) + col];
                if (Math.Abs(factor) < 1e-15)
                {
                    continue;
                }

                for (int c = col; c < 9; c++)
                {
                    mat[(r * 9) + c] -= factor * mat[(col * 9) + c];
                }
            }
        }

        homography = new Homography3x3(
            mat[(0 * 9) + 8], mat[(1 * 9) + 8], mat[(2 * 9) + 8],
            mat[(3 * 9) + 8], mat[(4 * 9) + 8], mat[(5 * 9) + 8],
            mat[(6 * 9) + 8], mat[(7 * 9) + 8], 1.0);
        return true;
    }

    public static int GetCharacterCountIndicatorBits(int modeIndicator, int version)
    {
        bool isSmall = version <= 9;
        return modeIndicator switch
        {
            0b0001 => isSmall ? 10 : 12, // Numeric
            0b0010 => isSmall ? 9 : 11,  // Alphanumeric
            0b0100 => isSmall ? 8 : 16,  // Byte
            0b1000 => isSmall ? 8 : 10,  // Kanji
            _ => 0
        };
    }

    private static void PlaceFinderWithSeparator(int n, int topRow, int leftCol, Span<bool> isFunction, Span<bool> expectedDark)
    {
        for (int dr = -1; dr <= 7; dr++)
        {
            int r = topRow + dr;
            if ((uint)r >= (uint)n)
            {
                continue;
            }

            for (int dc = -1; dc <= 7; dc++)
            {
                int c = leftCol + dc;
                if ((uint)c >= (uint)n)
                {
                    continue;
                }

                bool inFinder = (uint)dr < 7u && (uint)dc < 7u;
                bool dark = false;
                if (inFinder)
                {
                    int cheb = Math.Max(Math.Abs(dr - 3), Math.Abs(dc - 3));
                    dark = cheb != 2;
                }

                isFunction[(r * n) + c] = true;
                expectedDark[(r * n) + c] = dark;
            }
        }
    }

    private static ushort EncodeFormatBitsRaw(int data5)
    {
        int rem = (data5 & 0x1F) << FormatParityBitCount;
        for (int bit = (FormatDataBitCount + FormatParityBitCount) - 1; bit >= FormatParityBitCount; bit--)
        {
            if (((rem >> bit) & 1) != 0)
            {
                rem ^= FormatGeneratorPolynomial << (bit - FormatParityBitCount);
            }
        }

        int unmasked = ((data5 & 0x1F) << FormatParityBitCount) | rem;
        return (ushort)((unmasked ^ FormatXorMask) & 0x7FFF);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte GfMultiply(byte a, byte b)
        => (a == 0 || b == 0) ? (byte)0 : GfExp[GfLog[a] + GfLog[b]];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte GfDivide(byte a, byte b)
        => a == 0 ? (byte)0 : GfExp[(GfLog[a] + GfMaxElementIndex) - GfLog[b]];
}
