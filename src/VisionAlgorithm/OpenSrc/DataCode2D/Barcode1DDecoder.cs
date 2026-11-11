using System.Buffers;
using System.Runtime.CompilerServices;

namespace VisionAlgorithm.OpenSrc.DataCode2D;

/// <summary>
/// Multi-scanline sub-pixel 1D Barcode (ISO/IEC 15417 Code 128 Set B &amp; ISO/IEC 15420 EAN-13) decoder,
/// full 360-degree orientation estimator, and ISO/IEC 15416 print quality verifier.
/// </summary>
public static class Barcode1DDecoder
{
    private const int Code128StartCodeB = 104;
    private const int Code128StopCode = 106;
    private const int Code128Modulo = 103;
    private const int ModulesPerCharacter = 11;
    private const int ElementsPerCharacter = 6;
    private const int StopPatternElements = 7;
    private const int MaxRunsPerScanline = 512;
    private const int MaxScanlineSamples = 4096;
    private const int MinScanlines = 3;
    private const int MaxScanlines = 25;
    private const int DefaultScanlineCount = 9;
    private const byte MaxByteIntensity = byte.MaxValue;
    private const double HalfFactor = 0.5;
    private const double DegreesToRadians = Math.PI / 180.0;
    private const double ReorientThresholdRad = 1.0 * DegreesToRadians;
    private const double MinVarianceEpsilon = 1e-6;

    // Official ISO/IEC 15417 Code 128 bar/space width patterns (packed as 6 decimal digits: B1 S1 B2 S2 B3 S3).
    private static readonly int[] Code128Patterns =
    [
        212222, 222122, 222221, 121223, 121322, 131222, 122213, 122312, 132212, 221213,
        221312, 231212, 112232, 122132, 122231, 113222, 123122, 123221, 223211, 221132,
        221231, 213212, 223112, 312131, 311222, 321122, 321221, 312212, 322112, 322211,
        212123, 212321, 232121, 111323, 131123, 131321, 112313, 132113, 132311, 211313,
        231113, 231311, 112133, 112331, 132131, 113123, 113321, 133121, 313121, 211331,
        231131, 213113, 213311, 213131, 311123, 311321, 331121, 312113, 312311, 332111,
        314111, 221411, 431111, 111224, 111422, 121124, 121421, 141122, 141221, 112214,
        112412, 122114, 122411, 142112, 142211, 241211, 221114, 413111, 241112, 134111,
        111242, 121142, 121241, 114212, 124112, 124211, 411212, 421112, 421211, 212141,
        214121, 412121, 111143, 111341, 131141, 114113, 114311, 411113, 411311, 113141,
        114131, 311141, 411131, 211412, 211214, 211232, 233111
    ];

    // Official ISO/IEC 15420 EAN-13 4-element (S B S B / B S B S) run-width patterns for L-code, G-code, R-code digits 0..9.
    private static readonly int[] EanLPatterns = [3211, 2221, 2122, 1411, 1132, 1231, 1114, 1312, 1213, 3112];
    private static readonly int[] EanGPatterns = [1123, 1222, 2212, 1141, 2311, 1321, 4111, 2131, 3121, 2113];
    // 6-bit L(0)/G(1) parity masks for EAN-13 first digit 0..9
    private static readonly int[] EanFirstDigitParityMasks = [0b000000, 0b001011, 0b001101, 0b001110, 0b010011, 0b011001, 0b011100, 0b010101, 0b010110, 0b011010];

    /// <summary>
    /// Renders an axis-aligned ISO/IEC 15417 Code 128 Set B barcode into an 8-bit grayscale image buffer.
    /// </summary>
    public static void RenderCode128Symbol(
        Span<byte> canvas,
        int width,
        int height,
        int stride,
        string payload,
        int barLeftX,
        int barTopY,
        int barHeight,
        int moduleWidthPx = 2,
        byte backgroundIntensity = 238,
        byte barIntensity = 22)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, width);

        var safeModuleWidth = Math.Clamp(moduleWidthPx, 1, 8);
        Span<int> codes = stackalloc int[payload.Length + 3];
        BuildCode128Symbols(payload, codes);

        var y0 = Math.Clamp(barTopY, 0, height - 1);
        var y1 = Math.Clamp(barTopY + barHeight, y0 + 1, height);
        var cursorX = barLeftX;
        Span<int> widths = stackalloc int[StopPatternElements];

        for (var c = 0; c < codes.Length; c++)
        {
            var elementCount = UnpackCode128Widths(codes[c], isStop: c == codes.Length - 1, widths);
            for (var e = 0; e < elementCount; e++)
            {
                var isBar = (e & 1) == 0;
                var pxWidth = widths[e] * safeModuleWidth;
                var color = isBar ? barIntensity : backgroundIntensity;

                for (var y = y0; y < y1; y++)
                {
                    var row = y * stride;
                    for (var dx = 0; dx < pxWidth; dx++)
                    {
                        var x = cursorX + dx;
                        if ((uint)x < (uint)width)
                        {
                            canvas[row + x] = color;
                        }
                    }
                }

                cursorX += pxWidth;
            }
        }
    }

    /// <summary>
    /// Renders an arbitrarily rotated ISO/IEC 15417 Code 128 Set B barcode with 4x4 sub-pixel anti-aliasing
    /// centered at <paramref name="centerX"/>, <paramref name="centerY"/> with angle <paramref name="angleRad"/>.
    /// </summary>
    public static void RenderRotatedCode128Symbol(
        Span<byte> canvas,
        int width,
        int height,
        int stride,
        string payload,
        float centerX,
        float centerY,
        int barHeight,
        float angleRad,
        float moduleWidthPx = 2.5f,
        byte backgroundIntensity = 236,
        byte barIntensity = 22)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, width);

        float safeModule = Math.Clamp(moduleWidthPx, 1.5f, 10.0f);
        Span<int> codes = stackalloc int[payload.Length + 3];
        BuildCode128Symbols(payload, codes);

        int totalModules = ((codes.Length - 1) * ModulesPerCharacter) + 13;
        Span<bool> moduleIsBar = stackalloc bool[totalModules];
        Span<int> widths = stackalloc int[StopPatternElements];
        int mCursor = 0;
        for (int c = 0; c < codes.Length; c++)
        {
            int elemCount = UnpackCode128Widths(codes[c], isStop: c == codes.Length - 1, widths);
            for (int e = 0; e < elemCount; e++)
            {
                bool isBar = (e & 1) == 0;
                for (int k = 0; k < widths[e]; k++)
                {
                    moduleIsBar[mCursor++] = isBar;
                }
            }
        }

        float totalBarWidth = totalModules * safeModule;
        float halfW = totalBarWidth * 0.5f;
        float halfH = barHeight * 0.5f;
        float quietMargin = safeModule * 10.0f;
        float boxHalfU = halfW + quietMargin;
        float boxHalfV = halfH + 6.0f;

        (float sinA, float cosA) = MathF.SinCos(angleRad);
        float radX = (boxHalfU * MathF.Abs(cosA)) + (boxHalfV * MathF.Abs(sinA)) + 2.0f;
        float radY = (boxHalfU * MathF.Abs(sinA)) + (boxHalfV * MathF.Abs(cosA)) + 2.0f;

        int x0 = Math.Clamp((int)MathF.Floor(centerX - radX), 0, width - 1);
        int x1 = Math.Clamp((int)MathF.Ceiling(centerX + radX), 0, width - 1);
        int y0 = Math.Clamp((int)MathF.Floor(centerY - radY), 0, height - 1);
        int y1 = Math.Clamp((int)MathF.Ceiling(centerY + radY), 0, height - 1);

        const int subSamples = 4;
        const float invSub = 1.0f / subSamples;
        const float invTotalSamples = 1.0f / (subSamples * subSamples);

        for (int y = y0; y <= y1; y++)
        {
            int rowOffset = y * stride;
            for (int x = x0; x <= x1; x++)
            {
                int touched = 0;
                float accum = 0.0f;
                for (int sy = 0; sy < subSamples; sy++)
                {
                    float dy = (y + ((sy + 0.5f) * invSub)) - centerY;
                    for (int sx = 0; sx < subSamples; sx++)
                    {
                        float dx = (x + ((sx + 0.5f) * invSub)) - centerX;
                        float u = (dx * cosA) + (dy * sinA);
                        float v = (-dx * sinA) + (dy * cosA);
                        if (MathF.Abs(u) <= boxHalfU && MathF.Abs(v) <= boxHalfV)
                        {
                            touched++;
                            bool dark = false;
                            if (MathF.Abs(u) <= halfW && MathF.Abs(v) <= halfH)
                            {
                                int modIdx = (int)MathF.Floor((u + halfW) / safeModule);
                                if ((uint)modIdx < (uint)totalModules)
                                {
                                    dark = moduleIsBar[modIdx];
                                }
                            }
                            accum += dark ? barIntensity : backgroundIntensity;
                        }
                    }
                }

                if (touched == subSamples * subSamples)
                {
                    canvas[rowOffset + x] = (byte)Math.Clamp((int)MathF.Round(accum * invTotalSamples), 0, MaxByteIntensity);
                }
                else if (touched > 0)
                {
                    float existing = canvas[rowOffset + x] * ((subSamples * subSamples) - touched);
                    canvas[rowOffset + x] = (byte)Math.Clamp((int)MathF.Round((accum + existing) * invTotalSamples), 0, MaxByteIntensity);
                }
            }
        }
    }

    /// <summary>
    /// Decodes a 1D barcode (Code 128 Set B or EAN-13) across multiple parallel scanlines at arbitrary orientation
    /// (-180°..+180°), refines sub-degree rotation angle, and computes ISO/IEC 15416 print quality metrics.
    /// </summary>
    public static Barcode1DDecodeResult DecodeMultiScanline(
        ReadOnlySpan<byte> image,
        int width,
        int height,
        int stride,
        int scanlineCount = DefaultScanlineCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, width);
        if (image.Length < ((height - 1) * stride) + width)
        {
            throw new ArgumentException("Image buffer is smaller than (height - 1) * stride + width.", nameof(image));
        }

        var safeScanlines = Math.Clamp(scanlineCount, MinScanlines, MaxScanlines);
        double tensorAngle = EstimateDominantGradientAxis(image, width, height, stride);

        // Fast horizontal path when dominant bar gradient is near 0 deg (covers 0 deg and 180 deg without interpolation overhead)
        if (Math.Abs(tensorAngle) <= (1.2 * DegreesToRadians) &&
            TryDecodeAtAxisAngle(image, width, height, stride, axisAngleRad: 0.0, safeScanlines, out Barcode1DDecodeResult horizontalResult) &&
            horizontalResult.IsChecksumValid &&
            horizontalResult.DecodedScanlineCount >= Math.Max(2, safeScanlines / 2) &&
            Math.Abs(NormalizeHalfPi(horizontalResult.AngleRad)) <= ReorientThresholdRad)
        {
            return horizontalResult;
        }

        // General 360-degree path: dominant 2D Sobel gradient structure tensor axis + multi-angle candidate search (-90..+90 axis, forward & reverse)
        Span<double> candidateAxes = stackalloc double[18];
        int candCount = 0;
        AddUniqueAxisAngle(candidateAxes, ref candCount, tensorAngle);
        AddUniqueAxisAngle(candidateAxes, ref candCount, NormalizeHalfPi(tensorAngle - (2.5 * DegreesToRadians)));
        AddUniqueAxisAngle(candidateAxes, ref candCount, NormalizeHalfPi(tensorAngle + (2.5 * DegreesToRadians)));
        AddUniqueAxisAngle(candidateAxes, ref candCount, NormalizeHalfPi(tensorAngle - (5.0 * DegreesToRadians)));
        AddUniqueAxisAngle(candidateAxes, ref candCount, NormalizeHalfPi(tensorAngle + (5.0 * DegreesToRadians)));
        AddUniqueAxisAngle(candidateAxes, ref candCount, 0.0);

        for (int deg = -90; deg < 90; deg += 15)
        {
            AddUniqueAxisAngle(candidateAxes, ref candCount, deg * DegreesToRadians);
        }

        Barcode1DDecodeResult bestOriented = default;
        for (int i = 0; i < candCount; i++)
        {
            if (TryDecodeAtAxisAngle(image, width, height, stride, candidateAxes[i], safeScanlines, out Barcode1DDecodeResult cand))
            {
                if (!bestOriented.IsDecoded ||
                    (cand.IsChecksumValid && !bestOriented.IsChecksumValid) ||
                    (cand.IsChecksumValid == bestOriented.IsChecksumValid && cand.DecodedScanlineCount > bestOriented.DecodedScanlineCount) ||
                    (cand.IsChecksumValid == bestOriented.IsChecksumValid &&
                     cand.DecodedScanlineCount == bestOriented.DecodedScanlineCount &&
                     cand.OverallGradeScore > bestOriented.OverallGradeScore))
                {
                    bestOriented = cand;
                    if (bestOriented.IsChecksumValid && bestOriented.DecodedScanlineCount >= Math.Max(3, (safeScanlines * 2) / 3))
                    {
                        break;
                    }
                }
            }
        }

        if (bestOriented.IsDecoded)
        {
            double baseUsed = NormalizeHalfPi(bestOriented.AngleRad);
            if (TryDecodeAtAxisAngle(image, width, height, stride, baseUsed, safeScanlines, out Barcode1DDecodeResult polished) &&
                polished.IsChecksumValid == bestOriented.IsChecksumValid &&
                polished.DecodedScanlineCount >= bestOriented.DecodedScanlineCount)
            {
                return polished;
            }

            return bestOriented;
        }

        return new Barcode1DDecodeResult(
            IsDecoded: false,
            Symbology: "Code 128",
            Payload: string.Empty,
            ChecksumValue: 0,
            IsChecksumValid: false,
            DecodedScanlineCount: 0,
            SymbolContrast: 0.0,
            Modulation: 0.0,
            Decodability: 0.0,
            OverallGradeScore: 0.0,
            IsoGradeLetter: "F (0.0)",
            StartX: 0.0,
            EndX: 0.0,
            ScanlineY: height * HalfFactor);
    }

    private static bool TryDecodeAtAxisAngle(
        ReadOnlySpan<byte> image,
        int width,
        int height,
        int stride,
        double axisAngleRad,
        int safeScanlines,
        out Barcode1DDecodeResult result)
    {
        result = default;
        (double sinA, double cosA) = Math.SinCos(axisAngleRad);
        double cx0 = (width - 1) * HalfFactor;
        double cy0 = (height - 1) * HalfFactor;

        double maxHalfV = ((width * Math.Abs(sinA)) + (height * Math.Abs(cosA))) * 0.42;
        if (maxHalfV < 2.0)
        {
            return false;
        }

        Span<double> hitV = stackalloc double[MaxScanlines];
        Span<double> hitUStart = stackalloc double[MaxScanlines];
        Span<double> hitUEnd = stackalloc double[MaxScanlines];
        Span<bool> hitReversed = stackalloc bool[MaxScanlines];

        string bestPayload = string.Empty;
        string bestSymbology = "Code 128B";
        int bestChecksum = 0;
        bool bestChecksumValid = false;
        bool winningReverse = false;
        int decodedScanlines = 0;
        double sumContrast = 0.0;
        double sumModulation = 0.0;
        double sumDecodability = 0.0;

        byte[] rentedLine = ArrayPool<byte>.Shared.Rent(MaxScanlineSamples);
        byte[] rentedRev = ArrayPool<byte>.Shared.Rent(MaxScanlineSamples);
        try
        {
            for (int s = 0; s < safeScanlines; s++)
            {
                double t = safeScanlines == 1 ? 0.0 : (-1.0 + ((2.0 * s) / (safeScanlines - 1)));
                double vOffset = t * maxHalfV * 0.72;

                if (!SampleOrientedScanline(image, width, height, stride, cx0, cy0, cosA, sinA, vOffset, rentedLine, out int lineLen, out double uMin))
                {
                    continue;
                }

                ReadOnlySpan<byte> forwardSpan = rentedLine.AsSpan(0, lineLen);
                bool decodedThisLine = false;
                bool lineReversed = false;
                string payload = string.Empty;
                string symbology = "Code 128B";
                int checksumVal = 0;
                bool checksumOk = false;
                double contrast = 0.0;
                double mod = 0.0;
                double dec = 0.0;
                double localStart = 0.0;
                double localEnd = 0.0;

                if (TryDecodeScanlineCode128(forwardSpan, out payload, out checksumVal, out checksumOk, out contrast, out mod, out dec, out localStart, out localEnd))
                {
                    decodedThisLine = true;
                    symbology = "Code 128B";
                }
                else
                {
                    Span<byte> revSpan = rentedRev.AsSpan(0, lineLen);
                    for (int k = 0; k < lineLen; k++)
                    {
                        revSpan[k] = forwardSpan[lineLen - 1 - k];
                    }

                    if (TryDecodeScanlineCode128(revSpan, out payload, out checksumVal, out checksumOk, out contrast, out mod, out dec, out double revS, out double revE))
                    {
                        decodedThisLine = true;
                        lineReversed = true;
                        symbology = "Code 128B";
                        localStart = (lineLen - 1) - revE;
                        localEnd = (lineLen - 1) - revS;
                    }
                    else if (TryDecodeScanlineEan13(forwardSpan, out payload, out checksumVal, out checksumOk, out contrast, out mod, out dec, out localStart, out localEnd))
                    {
                        decodedThisLine = true;
                        symbology = "EAN-13";
                    }
                    else if (TryDecodeScanlineEan13(revSpan, out payload, out checksumVal, out checksumOk, out contrast, out mod, out dec, out double revEanS, out double revEanE))
                    {
                        decodedThisLine = true;
                        lineReversed = true;
                        symbology = "EAN-13";
                        localStart = (lineLen - 1) - revEanE;
                        localEnd = (lineLen - 1) - revEanS;
                    }
                }

                if (!decodedThisLine)
                {
                    continue;
                }

                double uStart = uMin + localStart;
                double uEnd = uMin + localEnd;
                hitV[decodedScanlines] = vOffset;
                hitUStart[decodedScanlines] = uStart;
                hitUEnd[decodedScanlines] = uEnd;
                hitReversed[decodedScanlines] = lineReversed;
                decodedScanlines++;

                sumContrast += contrast;
                sumModulation += mod;
                sumDecodability += dec;

                if (bestPayload.Length == 0 || (checksumOk && !bestChecksumValid) || (checksumOk == bestChecksumValid && payload.Length > bestPayload.Length))
                {
                    bestPayload = payload;
                    bestSymbology = symbology;
                    bestChecksum = checksumVal;
                    bestChecksumValid = checksumOk;
                    winningReverse = lineReversed;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rentedLine);
            ArrayPool<byte>.Shared.Return(rentedRev);
        }

        if (decodedScanlines == 0)
        {
            return false;
        }

        // Sub-degree linear regression of (v, uStart) and (v, uEnd) across matching-direction scanlines:
        // [Math Why]: A bar edge perpendicular to true axis (theta = alpha + delta) satisfies u*cos(delta) + v*sin(delta) = const => du/dv = -tan(delta).
        double sumV = 0.0;
        double sumUS = 0.0;
        double sumUE = 0.0;
        int regCount = 0;
        for (int i = 0; i < decodedScanlines; i++)
        {
            if (hitReversed[i] == winningReverse)
            {
                sumV += hitV[i];
                sumUS += hitUStart[i];
                sumUE += hitUEnd[i];
                regCount++;
            }
        }

        double deltaTheta = 0.0;
        double meanUStart = regCount > 0 ? sumUS / regCount : hitUStart[0];
        double meanUEnd = regCount > 0 ? sumUE / regCount : hitUEnd[0];
        double meanV = regCount > 0 ? sumV / regCount : hitV[0];

        if (regCount >= 2)
        {
            double varV = 0.0;
            double covVS = 0.0;
            double covVE = 0.0;
            for (int i = 0; i < decodedScanlines; i++)
            {
                if (hitReversed[i] != winningReverse)
                {
                    continue;
                }
                double dv = hitV[i] - meanV;
                varV += dv * dv;
                covVS += dv * (hitUStart[i] - meanUStart);
                covVE += dv * (hitUEnd[i] - meanUEnd);
            }

            if (varV > 4.0)
            {
                double slope = ((covVS / varV) + (covVE / varV)) * HalfFactor;
                if (Math.Abs(slope) < 0.45)
                {
                    deltaTheta = -Math.Atan(slope);
                }
            }
        }

        double trueAxisAngle = axisAngleRad + deltaTheta;
        double fullAngleRad = NormalizeAngleRad(winningReverse ? (trueAxisAngle + Math.PI) : trueAxisAngle);

        double uMid = (meanUStart + meanUEnd) * HalfFactor;
        double seedWorldX = cx0 + (uMid * cosA) - (meanV * sinA);
        double seedWorldY = cy0 + (uMid * sinA) + (meanV * cosA);

        (double sinTrue, double cosTrue) = Math.SinCos(trueAxisAngle);
        double orientedW = Math.Max(1.0, Math.Abs(meanUEnd - meanUStart) * Math.Cos(deltaTheta));
        double halfTrueW = orientedW * HalfFactor;
        double seedTrueU = ((seedWorldX - cx0) * cosTrue) + ((seedWorldY - cy0) * sinTrue);
        double seedTrueV = (-(seedWorldX - cx0) * sinTrue) + ((seedWorldY - cy0) * cosTrue);

        RefineOrientedBarExtent(
            image,
            width,
            height,
            stride,
            cx0,
            cy0,
            cosTrue,
            sinTrue,
            seedTrueU - halfTrueW,
            seedTrueU + halfTrueW,
            seedTrueV,
            out double vMinBar,
            out double vMaxBar);

        double vMid = (vMinBar + vMaxBar) * HalfFactor;
        double worldCenterX = cx0 + (seedTrueU * cosTrue) - (vMid * sinTrue);
        double worldCenterY = cy0 + (seedTrueU * sinTrue) + (vMid * cosTrue);
        double orientedH = Math.Max(1.0, vMaxBar - vMinBar);

        // Compute axis-aligned extremes from the 4 oriented corners for legacy StartX/EndX/TopY/BottomY compatibility
        (double sinFull, double cosFull) = Math.SinCos(fullAngleRad);
        double hw = orientedW * HalfFactor;
        double hh = orientedH * HalfFactor;
        double dxU = hw * Math.Abs(cosFull);
        double dxV = hh * Math.Abs(sinFull);
        double dyU = hw * Math.Abs(sinFull);
        double dyV = hh * Math.Abs(cosFull);

        double aabbLeft = worldCenterX - dxU - dxV;
        double aabbRight = worldCenterX + dxU + dxV;
        double aabbTop = worldCenterY - dyU - dyV;
        double aabbBottom = worldCenterY + dyU + dyV;

        double avgContrast = sumContrast / decodedScanlines;
        double avgModulation = sumModulation / decodedScanlines;
        double avgDecodability = sumDecodability / decodedScanlines;

        double overallGrade = Math.Min(ScoreContrast(avgContrast), Math.Min(ScoreModulation(avgModulation), ScoreDecodability(avgDecodability)));
        if (!bestChecksumValid)
        {
            overallGrade = Math.Min(overallGrade, 1.0);
        }

        string gradeLetter = overallGrade switch
        {
            >= 3.5 => $"A ({overallGrade:F1})",
            >= 2.5 => $"B ({overallGrade:F1})",
            >= 1.5 => $"C ({overallGrade:F1})",
            >= 0.5 => $"D ({overallGrade:F1})",
            _ => $"F ({overallGrade:F1})"
        };

        result = new Barcode1DDecodeResult(
            IsDecoded: true,
            Symbology: bestSymbology,
            Payload: bestPayload,
            ChecksumValue: bestChecksum,
            IsChecksumValid: bestChecksumValid,
            DecodedScanlineCount: decodedScanlines,
            SymbolContrast: avgContrast,
            Modulation: avgModulation,
            Decodability: avgDecodability,
            OverallGradeScore: overallGrade,
            IsoGradeLetter: gradeLetter,
            StartX: aabbLeft,
            EndX: aabbRight,
            ScanlineY: worldCenterY,
            TopY: aabbTop,
            BottomY: aabbBottom,
            AngleRad: fullAngleRad,
            OrientedCenterX: worldCenterX,
            OrientedCenterY: worldCenterY,
            OrientedWidth: orientedW,
            OrientedHeight: orientedH);
        return true;
    }

    private static bool SampleOrientedScanline(
        ReadOnlySpan<byte> image,
        int width,
        int height,
        int stride,
        double cx0,
        double cy0,
        double cosA,
        double sinA,
        double vOffset,
        Span<byte> destination,
        out int length,
        out double uMinOut)
    {
        length = 0;
        uMinOut = 0.0;

        // Exact integer row copy fast-path when axisAngle == 0
        if (Math.Abs(sinA) < 1e-7 && Math.Abs(cosA - 1.0) < 1e-7)
        {
            int y = (int)Math.Round(cy0 + vOffset);
            if ((uint)y >= (uint)height || width > destination.Length)
            {
                return false;
            }
            image.Slice(y * stride, width).CopyTo(destination);
            length = width;
            uMinOut = -cx0;
            return length >= 24;
        }

        double baseX = cx0 - (vOffset * sinA);
        double baseY = cy0 + (vOffset * cosA);
        double uMin = -4096.0;
        double uMax = 4096.0;

        if (!ClipAxisInterval(baseX, cosA, 0.0, width - 1.001, ref uMin, ref uMax) ||
            !ClipAxisInterval(baseY, sinA, 0.0, height - 1.001, ref uMin, ref uMax))
        {
            return false;
        }

        int count = (int)Math.Floor(uMax - uMin) + 1;
        if (count < 24)
        {
            return false;
        }
        if (count > destination.Length)
        {
            count = destination.Length;
        }

        uMinOut = uMin;
        length = count;
        for (int i = 0; i < count; i++)
        {
            double u = uMin + i;
            double sx = baseX + (u * cosA);
            double sy = baseY + (u * sinA);
            destination[i] = SampleBilinearByte(image, width, height, stride, sx, sy);
        }

        return true;
    }

    private static bool ClipAxisInterval(double origin, double dir, double minBound, double maxBound, ref double tMin, ref double tMax)
    {
        if (Math.Abs(dir) < 1e-9)
        {
            return origin >= minBound && origin <= maxBound;
        }

        double t0 = (minBound - origin) / dir;
        double t1 = (maxBound - origin) / dir;
        if (t0 > t1)
        {
            (t0, t1) = (t1, t0);
        }
        if (t0 > tMin) tMin = t0;
        if (t1 < tMax) tMax = t1;
        return tMax >= tMin;
    }

    private static void RefineOrientedBarExtent(
        ReadOnlySpan<byte> image,
        int width,
        int height,
        int stride,
        double cx0,
        double cy0,
        double cosA,
        double sinA,
        double uStart,
        double uEnd,
        double seedV,
        out double vTop,
        out double vBottom)
    {
        double uLo = Math.Min(uStart, uEnd);
        double uHi = Math.Max(uStart, uEnd);
        double spanU = Math.Max(4.0, uHi - uLo);
        int samples = Math.Clamp((int)Math.Round(spanU), 16, 160);
        double stepU = spanU / (samples - 1);

        byte minV = MaxByteIntensity;
        byte maxV = 0;
        for (int i = 0; i < samples; i++)
        {
            double u = uLo + (i * stepU);
            double sx = cx0 + (u * cosA) - (seedV * sinA);
            double sy = cy0 + (u * sinA) + (seedV * cosA);
            if (sx < 0 || sx >= width - 1 || sy < 0 || sy >= height - 1)
            {
                continue;
            }
            byte val = SampleBilinearByte(image, width, height, stride, sx, sy);
            if (val < minV) minV = val;
            if (val > maxV) maxV = val;
        }

        double mid = (minV + maxV) * HalfFactor;
        int minDarkHits = Math.Max(4, samples / 4);
        int maxReach = Math.Max(width, height);

        double curTop = seedV;
        for (int step = 1; step < maxReach; step++)
        {
            double testV = seedV - step;
            int darkHits = 0;
            int inBounds = 0;
            for (int i = 0; i < samples; i++)
            {
                double u = uLo + (i * stepU);
                double sx = cx0 + (u * cosA) - (testV * sinA);
                double sy = cy0 + (u * sinA) + (testV * cosA);
                if (sx < 0 || sx > width - 1 || sy < 0 || sy > height - 1)
                {
                    continue;
                }
                inBounds++;
                if (SampleBilinearByte(image, width, height, stride, sx, sy) < mid)
                {
                    darkHits++;
                }
            }

            if (inBounds < samples / 2 || darkHits < minDarkHits)
            {
                break;
            }
            curTop = testV;
        }

        double curBottom = seedV;
        for (int step = 1; step < maxReach; step++)
        {
            double testV = seedV + step;
            int darkHits = 0;
            int inBounds = 0;
            for (int i = 0; i < samples; i++)
            {
                double u = uLo + (i * stepU);
                double sx = cx0 + (u * cosA) - (testV * sinA);
                double sy = cy0 + (u * sinA) + (testV * cosA);
                if (sx < 0 || sx > width - 1 || sy < 0 || sy > height - 1)
                {
                    continue;
                }
                inBounds++;
                if (SampleBilinearByte(image, width, height, stride, sx, sy) < mid)
                {
                    darkHits++;
                }
            }

            if (inBounds < samples / 2 || darkHits < minDarkHits)
            {
                break;
            }
            curBottom = testV;
        }

        vTop = curTop;
        vBottom = curBottom + 1.0;
    }

    private static double EstimateDominantGradientAxis(ReadOnlySpan<byte> image, int width, int height, int stride)
    {
        if (width < 4 || height < 4)
        {
            return 0.0;
        }

        double jxx = 0.0;
        double jyy = 0.0;
        double jxy = 0.0;
        int stepY = height > 240 ? 2 : 1;
        int stepX = width > 240 ? 2 : 1;

        for (int y = 1; y < height - 1; y += stepY)
        {
            ReadOnlySpan<byte> rowPrev = image.Slice((y - 1) * stride, width);
            ReadOnlySpan<byte> rowCurr = image.Slice(y * stride, width);
            ReadOnlySpan<byte> rowNext = image.Slice((y + 1) * stride, width);

            for (int x = 1; x < width - 1; x += stepX)
            {
                double gx = rowCurr[x + 1] - rowCurr[x - 1];
                double gy = rowNext[x] - rowPrev[x];
                double magSq = (gx * gx) + (gy * gy);
                if (magSq < 400.0)
                {
                    continue;
                }

                jxx += gx * gx;
                jyy += gy * gy;
                jxy += gx * gy;
            }
        }

        if (jxx + jyy < MinVarianceEpsilon)
        {
            return 0.0;
        }

        return NormalizeHalfPi(HalfFactor * Math.Atan2(2.0 * jxy, jxx - jyy));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte SampleBilinearByte(ReadOnlySpan<byte> image, int width, int height, int stride, double x, double y)
    {
        int x0 = Math.Clamp((int)Math.Floor(x), 0, width - 1);
        int y0 = Math.Clamp((int)Math.Floor(y), 0, height - 1);
        int x1 = Math.Min(width - 1, x0 + 1);
        int y1 = Math.Min(height - 1, y0 + 1);

        double fx = Math.Clamp(x - x0, 0.0, 1.0);
        double fy = Math.Clamp(y - y0, 0.0, 1.0);

        int r0 = y0 * stride;
        int r1 = y1 * stride;
        double top = image[r0 + x0] + (fx * (image[r0 + x1] - image[r0 + x0]));
        double bot = image[r1 + x0] + (fx * (image[r1 + x1] - image[r1 + x0]));
        return (byte)Math.Clamp((int)Math.Round(top + (fy * (bot - top))), 0, MaxByteIntensity);
    }

    private static void AddUniqueAxisAngle(Span<double> list, ref int count, double angleRad)
    {
        double norm = NormalizeHalfPi(angleRad);
        for (int i = 0; i < count; i++)
        {
            double diff = Math.Abs(NormalizeHalfPi(list[i] - norm));
            if (diff < (1.2 * DegreesToRadians))
            {
                return;
            }
        }

        if (count < list.Length)
        {
            list[count++] = norm;
        }
    }

    private static double NormalizeHalfPi(double rad)
    {
        while (rad >= Math.PI * HalfFactor) rad -= Math.PI;
        while (rad < -Math.PI * HalfFactor) rad += Math.PI;
        return rad;
    }

    private static double NormalizeAngleRad(double rad)
    {
        while (rad > Math.PI) rad -= 2.0 * Math.PI;
        while (rad <= -Math.PI) rad += 2.0 * Math.PI;
        return rad;
    }

    private static void BuildCode128Symbols(string payload, Span<int> codes)
    {
        codes[0] = Code128StartCodeB;
        int checksum = Code128StartCodeB;
        for (int i = 0; i < payload.Length; i++)
        {
            int codeVal = Math.Clamp(payload[i] - 32, 0, 94);
            codes[i + 1] = codeVal;
            checksum += codeVal * (i + 1);
        }

        codes[payload.Length + 1] = checksum % Code128Modulo;
        codes[payload.Length + 2] = Code128StopCode;
    }

    private static int UnpackCode128Widths(int code, bool isStop, Span<int> widths)
    {
        int temp = Code128Patterns[code];
        for (int e = ElementsPerCharacter - 1; e >= 0; e--)
        {
            widths[e] = temp % 10;
            temp /= 10;
        }

        if (isStop)
        {
            widths[6] = 2;
            return StopPatternElements;
        }

        return ElementsPerCharacter;
    }

    private static bool TryDecodeScanlineCode128(
        ReadOnlySpan<byte> row,
        out string payload,
        out int checksumValue,
        out bool isChecksumValid,
        out double symbolContrast,
        out double modulation,
        out double decodability,
        out double startX,
        out double endX)
    {
        payload = string.Empty;
        checksumValue = 0;
        isChecksumValid = false;
        symbolContrast = 0.0;
        modulation = 0.0;
        decodability = 0.0;
        startX = 0.0;
        endX = 0.0;

        int runCount = ExtractSubpixelRuns(
            row,
            stackalloc double[MaxRunsPerScanline],
            stackalloc bool[MaxRunsPerScanline],
            stackalloc double[MaxRunsPerScanline],
            stackalloc double[MaxRunsPerScanline],
            out Span<double> widths,
            out Span<bool> isBar,
            out Span<double> startEdges,
            out Span<double> peakDeltas,
            out int dynamicRange);

        if (runCount < (ElementsPerCharacter * 3) + 1)
        {
            return false;
        }

        symbolContrast = (double)dynamicRange / MaxByteIntensity;

        var startRunIdx = -1;
        for (var r = 0; r <= runCount - ElementsPerCharacter; r++)
        {
            if (!isBar[r])
            {
                continue;
            }

            var code = MatchCode128Character(widths.Slice(r, ElementsPerCharacter), out var charDecodability);
            if (code == Code128StartCodeB && charDecodability >= 0.22)
            {
                startRunIdx = r;
                break;
            }
        }

        if (startRunIdx < 0)
        {
            return false;
        }

        Span<int> decodedCodes = stackalloc int[64];
        var codeCount = 0;
        var cursor = startRunIdx;
        var minDecodability = 1.0;
        var minPeakDelta = dynamicRange * HalfFactor;
        var foundStop = false;

        while (cursor + ElementsPerCharacter <= runCount && codeCount < decodedCodes.Length)
        {
            var code = MatchCode128Character(widths.Slice(cursor, ElementsPerCharacter), out var charDecodability);
            if (code < 0)
            {
                break;
            }

            for (var e = 0; e < ElementsPerCharacter; e++)
            {
                if (peakDeltas[cursor + e] < minPeakDelta)
                {
                    minPeakDelta = peakDeltas[cursor + e];
                }
            }

            if (charDecodability < minDecodability)
            {
                minDecodability = charDecodability;
            }

            decodedCodes[codeCount++] = code;
            if (code == Code128StopCode)
            {
                foundStop = true;
                cursor += Math.Min(StopPatternElements, runCount - cursor);
                break;
            }

            cursor += ElementsPerCharacter;
        }

        if (!foundStop || codeCount < 4)
        {
            return false;
        }

        var dataCount = codeCount - 3;
        Span<char> chars = stackalloc char[dataCount];
        var computedSum = decodedCodes[0];

        for (var i = 0; i < dataCount; i++)
        {
            var codeVal = decodedCodes[i + 1];
            if (codeVal is < 0 or > 94)
            {
                return false;
            }

            chars[i] = (char)(codeVal + 32);
            computedSum += codeVal * (i + 1);
        }

        checksumValue = decodedCodes[codeCount - 2];
        isChecksumValid = (computedSum % Code128Modulo) == checksumValue;
        payload = new string(chars);
        decodability = Math.Clamp(minDecodability, 0.0, 1.0);
        modulation = Math.Clamp((2.0 * minPeakDelta) / Math.Max(1.0, dynamicRange), 0.0, 1.0);
        startX = startEdges[startRunIdx];
        endX = startEdges[Math.Min(cursor - 1, runCount - 1)] + widths[Math.Min(cursor - 1, runCount - 1)];
        return true;
    }

    private static bool TryDecodeScanlineEan13(
        ReadOnlySpan<byte> row,
        out string payload,
        out int checksumValue,
        out bool isChecksumValid,
        out double symbolContrast,
        out double modulation,
        out double decodability,
        out double startX,
        out double endX)
    {
        payload = string.Empty;
        checksumValue = 0;
        isChecksumValid = false;
        symbolContrast = 0.0;
        modulation = 0.0;
        decodability = 0.0;
        startX = 0.0;
        endX = 0.0;

        // EAN-13 requires 59 alternating elements: 3 (start 101) + 24 (6x4 left) + 5 (center 01010) + 24 (6x4 right) + 3 (end 101)
        const int eanTotalElements = 59;
        int runCount = ExtractSubpixelRuns(
            row,
            stackalloc double[MaxRunsPerScanline],
            stackalloc bool[MaxRunsPerScanline],
            stackalloc double[MaxRunsPerScanline],
            stackalloc double[MaxRunsPerScanline],
            out Span<double> widths,
            out Span<bool> isBar,
            out Span<double> startEdges,
            out Span<double> peakDeltas,
            out int dynamicRange);

        if (runCount < eanTotalElements)
        {
            return false;
        }

        symbolContrast = (double)dynamicRange / MaxByteIntensity;
        Span<int> digits = stackalloc int[13];

        for (int r = 0; r <= runCount - eanTotalElements; r++)
        {
            if (!isBar[r])
            {
                continue;
            }

            double totalW = 0.0;
            for (int i = 0; i < eanTotalElements; i++)
            {
                totalW += widths[r + i];
            }
            double modW = totalW / 95.0;
            if (modW < 0.75)
            {
                continue;
            }

            // Check start guard (1-1-1), center guard (1-1-1-1-1 at offset 27), end guard (1-1-1 at offset 56)
            if (Math.Abs((widths[r] / modW) - 1.0) > 0.65 ||
                Math.Abs((widths[r + 1] / modW) - 1.0) > 0.65 ||
                Math.Abs((widths[r + 2] / modW) - 1.0) > 0.65 ||
                Math.Abs((widths[r + 56] / modW) - 1.0) > 0.65 ||
                Math.Abs((widths[r + 57] / modW) - 1.0) > 0.65 ||
                Math.Abs((widths[r + 58] / modW) - 1.0) > 0.65)
            {
                continue;
            }

            int parityMask = 0;
            bool ok = true;
            double minDec = 1.0;

            for (int d = 0; d < 6; d++)
            {
                int packed = QuantizeFourElementDigit(widths.Slice(r + 3 + (d * 4), 4), out double charDec);
                int digit = MatchEanLeftDigit(packed, out bool isG);
                if (digit < 0)
                {
                    ok = false;
                    break;
                }
                digits[d + 1] = digit;
                parityMask = (parityMask << 1) | (isG ? 1 : 0);
                if (charDec < minDec) minDec = charDec;
            }

            if (!ok)
            {
                continue;
            }

            int firstDigit = -1;
            for (int fd = 0; fd < EanFirstDigitParityMasks.Length; fd++)
            {
                if (EanFirstDigitParityMasks[fd] == parityMask)
                {
                    firstDigit = fd;
                    break;
                }
            }
            if (firstDigit < 0)
            {
                continue;
            }
            digits[0] = firstDigit;

            for (int d = 0; d < 6; d++)
            {
                int packed = QuantizeFourElementDigit(widths.Slice(r + 32 + (d * 4), 4), out double charDec);
                int digit = MatchEanRightDigit(packed);
                if (digit < 0)
                {
                    ok = false;
                    break;
                }
                digits[d + 7] = digit;
                if (charDec < minDec) minDec = charDec;
            }

            if (!ok)
            {
                continue;
            }

            int weightedSum = 0;
            for (int k = 0; k < 12; k++)
            {
                weightedSum += digits[k] * ((k & 1) == 0 ? 1 : 3);
            }
            int expectedCheck = (10 - (weightedSum % 10)) % 10;
            if (digits[12] != expectedCheck)
            {
                continue;
            }

            double minPeak = dynamicRange * HalfFactor;
            for (int i = 0; i < eanTotalElements; i++)
            {
                if (peakDeltas[r + i] < minPeak) minPeak = peakDeltas[r + i];
            }

            Span<char> chars = stackalloc char[13];
            for (int k = 0; k < 13; k++)
            {
                chars[k] = (char)('0' + digits[k]);
            }

            payload = new string(chars);
            checksumValue = digits[12];
            isChecksumValid = true;
            decodability = Math.Clamp(minDec, 0.0, 1.0);
            modulation = Math.Clamp((2.0 * minPeak) / Math.Max(1.0, dynamicRange), 0.0, 1.0);
            startX = startEdges[r];
            endX = startEdges[r + eanTotalElements - 1] + widths[r + eanTotalElements - 1];
            return true;
        }

        return false;
    }

    private static int ExtractSubpixelRuns(
        ReadOnlySpan<byte> row,
        Span<double> widthsBuf,
        Span<bool> isBarBuf,
        Span<double> startEdgesBuf,
        Span<double> peakDeltasBuf,
        out Span<double> widths,
        out Span<bool> isBar,
        out Span<double> startEdges,
        out Span<double> peakDeltas,
        out int dynamicRange)
    {
        widths = widthsBuf;
        isBar = isBarBuf;
        startEdges = startEdgesBuf;
        peakDeltas = peakDeltasBuf;
        dynamicRange = 0;

        if (row.Length < 16)
        {
            return 0;
        }

        byte minR = MaxByteIntensity;
        byte maxR = 0;
        for (int x = 0; x < row.Length; x++)
        {
            byte v = row[x];
            if (v < minR) minR = v;
            if (v > maxR) maxR = v;
        }

        dynamicRange = maxR - minR;
        if (dynamicRange < 35)
        {
            return 0;
        }

        double globalThreshold = minR + (dynamicRange * HalfFactor);
        Span<double> transitions = stackalloc double[MaxRunsPerScanline];
        Span<bool> runIsBar = stackalloc bool[MaxRunsPerScanline];
        Span<double> runPeakDelta = stackalloc double[MaxRunsPerScanline];

        int transitionCount = 0;
        bool inBar = row[0] < globalThreshold;
        double currentPeakDelta = Math.Abs(row[0] - globalThreshold);

        for (int x = 1; x < row.Length; x++)
        {
            double prev = row[x - 1];
            double curr = row[x];
            bool nowBar = curr < globalThreshold;
            double delta = Math.Abs(curr - globalThreshold);
            if (delta > currentPeakDelta)
            {
                currentPeakDelta = delta;
            }

            if (nowBar != inBar)
            {
                double denom = Math.Abs(curr - prev);
                double frac = denom > MinVarianceEpsilon ? Math.Clamp((globalThreshold - prev) / (curr - prev), 0.0, 1.0) : HalfFactor;
                double edgeX = (x - 1) + frac;

                if (transitionCount < MaxRunsPerScanline)
                {
                    transitions[transitionCount] = edgeX;
                    runIsBar[transitionCount] = !nowBar;
                    runPeakDelta[transitionCount] = currentPeakDelta;
                    transitionCount++;
                }

                inBar = nowBar;
                currentPeakDelta = delta;
            }
        }

        int runCount = 0;
        for (int t = 0; t < transitionCount - 1; t++)
        {
            double w = transitions[t + 1] - transitions[t];
            if (w <= 0.25)
            {
                continue;
            }

            widthsBuf[runCount] = w;
            isBarBuf[runCount] = runIsBar[t + 1];
            startEdgesBuf[runCount] = transitions[t];
            peakDeltasBuf[runCount] = runPeakDelta[t + 1];
            runCount++;
        }

        return runCount;
    }

    private static int QuantizeFourElementDigit(ReadOnlySpan<double> elemWidths, out double decodability)
    {
        double total = elemWidths[0] + elemWidths[1] + elemWidths[2] + elemWidths[3];
        if (total <= 1.0)
        {
            decodability = 0.0;
            return 0;
        }

        double mod = total / 7.0;
        Span<int> q = stackalloc int[4];
        int sum = 0;
        double maxErr = 0.0;
        for (int i = 0; i < 4; i++)
        {
            double exact = elemWidths[i] / mod;
            int qi = Math.Clamp((int)Math.Round(exact), 1, 4);
            double err = Math.Abs(exact - qi);
            if (err > maxErr) maxErr = err;
            q[i] = qi;
            sum += qi;
        }

        while (sum != 7)
        {
            int bestIdx = 0;
            double bestRes = -10.0;
            bool inc = sum < 7;
            for (int i = 0; i < 4; i++)
            {
                double exact = elemWidths[i] / mod;
                double res = inc ? (exact - q[i]) : (q[i] - exact);
                bool can = inc ? q[i] < 4 : q[i] > 1;
                if (can && res > bestRes)
                {
                    bestRes = res;
                    bestIdx = i;
                }
            }
            if (bestRes < -5.0) break;
            q[bestIdx] += inc ? 1 : -1;
            sum += inc ? 1 : -1;
        }

        decodability = Math.Clamp(1.0 - (maxErr * 1.6), 0.05, 1.0);
        return (((((q[0] * 10) + q[1]) * 10) + q[2]) * 10) + q[3];
    }

    private static int MatchEanLeftDigit(int packed, out bool isGCode)
    {
        for (int d = 0; d < 10; d++)
        {
            if (EanLPatterns[d] == packed)
            {
                isGCode = false;
                return d;
            }
            if (EanGPatterns[d] == packed)
            {
                isGCode = true;
                return d;
            }
        }

        isGCode = false;
        return -1;
    }

    private static int MatchEanRightDigit(int packed)
    {
        // R-code run widths equal L-code run widths (inverted bar/space polarity, identical width tuple)
        for (int d = 0; d < 10; d++)
        {
            if (EanLPatterns[d] == packed)
            {
                return d;
            }
        }

        return -1;
    }

    private static int MatchCode128Character(ReadOnlySpan<double> elementWidths, out double decodability)
    {
        var totalWidth = 0.0;
        for (var i = 0; i < ElementsPerCharacter; i++)
        {
            totalWidth += elementWidths[i];
        }

        if (totalWidth <= 1.0)
        {
            decodability = 0.0;
            return -1;
        }

        var moduleWidth = totalWidth / ModulesPerCharacter;
        Span<int> quantized = stackalloc int[ElementsPerCharacter];
        var sumQuantized = 0;
        var maxModuleError = 0.0;

        for (var i = 0; i < ElementsPerCharacter; i++)
        {
            var exactModules = elementWidths[i] / moduleWidth;
            var q = Math.Clamp((int)Math.Round(exactModules), 1, 4);
            var err = Math.Abs(exactModules - q);
            if (err > maxModuleError)
            {
                maxModuleError = err;
            }

            quantized[i] = q;
            sumQuantized += q;
        }

        while (sumQuantized != ModulesPerCharacter)
        {
            var bestAdjustIdx = 0;
            var bestResidual = -10.0;
            var needIncrease = sumQuantized < ModulesPerCharacter;

            for (var i = 0; i < ElementsPerCharacter; i++)
            {
                var exactModules = elementWidths[i] / moduleWidth;
                var residual = needIncrease ? exactModules - quantized[i] : quantized[i] - exactModules;
                var canAdjust = needIncrease ? quantized[i] < 4 : quantized[i] > 1;
                if (canAdjust && residual > bestResidual)
                {
                    bestResidual = residual;
                    bestAdjustIdx = i;
                }
            }

            if (bestResidual < -5.0)
            {
                break;
            }

            quantized[bestAdjustIdx] += needIncrease ? 1 : -1;
            sumQuantized += needIncrease ? 1 : -1;
        }

        var packed = 0;
        for (var i = 0; i < ElementsPerCharacter; i++)
        {
            packed = (packed * 10) + quantized[i];
        }

        decodability = Math.Clamp(1.0 - (maxModuleError * 1.6), 0.05, 1.0);

        for (var c = 0; c < Code128Patterns.Length; c++)
        {
            if (Code128Patterns[c] == packed)
            {
                return c;
            }
        }

        return -1;
    }

    private static double ScoreContrast(double sc) => sc switch
    {
        >= 0.70 => 4.0,
        >= 0.55 => 3.0,
        >= 0.40 => 2.0,
        >= 0.20 => 1.0,
        _ => 0.0
    };

    private static double ScoreModulation(double mod) => mod switch
    {
        >= 0.70 => 4.0,
        >= 0.60 => 3.0,
        >= 0.50 => 2.0,
        >= 0.40 => 1.0,
        _ => 0.0
    };

    private static double ScoreDecodability(double dec) => dec switch
    {
        >= 0.62 => 4.0,
        >= 0.50 => 3.0,
        >= 0.37 => 2.0,
        >= 0.25 => 1.0,
        _ => 0.0
    };
}
