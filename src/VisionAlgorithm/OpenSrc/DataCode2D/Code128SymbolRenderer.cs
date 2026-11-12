namespace VisionAlgorithm.OpenSrc.DataCode2D;

/// <summary>
/// Renders ISO/IEC 15417 Code 128 Set B 1D barcode symbols (axis-aligned and 4x4 supersampled rotated)
/// into 8-bit grayscale image buffers.
/// </summary>
public static class Code128SymbolRenderer
{
    private const int Code128StartCodeB = 104;
    private const int Code128StopCode = 106;
    private const int Code128Modulo = 103;
    private const int AsciiPrintableBase = 32;
    private const int MaxCode128SetBValue = 94;
    private const int ModulesPerCharacter = 11;
    private const int StopCharacterModules = 13;
    private const int ElementsPerCharacter = 6;
    private const int StopPatternElements = 7;
    private const int FinalStopBarWidthModules = 2;
    private const int MinAxisAlignedModuleWidthPx = 1;
    private const int MaxAxisAlignedModuleWidthPx = 8;
    private const float MinRotatedModuleWidthPx = 1.5f;
    private const float MaxRotatedModuleWidthPx = 10.0f;
    private const float HalfFactor = 0.5f;
    private const float QuietMarginModules = 10.0f;
    private const float VerticalQuietPaddingPx = 6.0f;
    private const float BoundingBoxGuardPaddingPx = 2.0f;
    private const byte MaxByteIntensity = byte.MaxValue;
    private const byte DefaultAxisAlignedBackground = 238;
    private const byte DefaultRotatedBackground = 236;
    private const byte DefaultBarIntensity = 22;

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
        byte backgroundIntensity = DefaultAxisAlignedBackground,
        byte barIntensity = DefaultBarIntensity)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, width);

        var safeModuleWidth = Math.Clamp(moduleWidthPx, MinAxisAlignedModuleWidthPx, MaxAxisAlignedModuleWidthPx);
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
        byte backgroundIntensity = DefaultRotatedBackground,
        byte barIntensity = DefaultBarIntensity)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, width);

        float safeModule = Math.Clamp(moduleWidthPx, MinRotatedModuleWidthPx, MaxRotatedModuleWidthPx);
        Span<int> codes = stackalloc int[payload.Length + 3];
        BuildCode128Symbols(payload, codes);

        int totalModules = ((codes.Length - 1) * ModulesPerCharacter) + StopCharacterModules;
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
        float halfW = totalBarWidth * HalfFactor;
        float halfH = barHeight * HalfFactor;
        float quietMargin = safeModule * QuietMarginModules;
        float boxHalfU = halfW + quietMargin;
        float boxHalfV = halfH + VerticalQuietPaddingPx;

        (float sinA, float cosA) = MathF.SinCos(angleRad);
        float radX = (boxHalfU * MathF.Abs(cosA)) + (boxHalfV * MathF.Abs(sinA)) + BoundingBoxGuardPaddingPx;
        float radY = (boxHalfU * MathF.Abs(sinA)) + (boxHalfV * MathF.Abs(cosA)) + BoundingBoxGuardPaddingPx;

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
                    float dy = (y + ((sy + HalfFactor) * invSub)) - centerY;
                    for (int sx = 0; sx < subSamples; sx++)
                    {
                        float dx = (x + ((sx + HalfFactor) * invSub)) - centerX;
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

    private static void BuildCode128Symbols(string payload, Span<int> codes)
    {
        codes[0] = Code128StartCodeB;
        int checksum = Code128StartCodeB;
        for (int i = 0; i < payload.Length; i++)
        {
            int codeVal = Math.Clamp(payload[i] - AsciiPrintableBase, 0, MaxCode128SetBValue);
            codes[i + 1] = codeVal;
            checksum += codeVal * (i + 1);
        }

        codes[payload.Length + 1] = checksum % Code128Modulo;
        codes[payload.Length + 2] = Code128StopCode;
    }

    private static int UnpackCode128Widths(int code, bool isStop, Span<int> widths)
    {
        int temp = Barcode1DDecoder.Code128Patterns[code];
        for (int e = ElementsPerCharacter - 1; e >= 0; e--)
        {
            widths[e] = temp % 10;
            temp /= 10;
        }

        if (isStop)
        {
            widths[ElementsPerCharacter] = FinalStopBarWidthModules;
            return StopPatternElements;
        }

        return ElementsPerCharacter;
    }
}
