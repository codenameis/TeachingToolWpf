namespace VisionAlgorithm.OpenSrc.AutoTeaching;

/// <summary>
/// Renders anti-aliased analytical SDF reference part archetypes (Bracket, Connector, ShieldCan)
/// used as standard baseline templates for <see cref="MultiObjectAutoInspectionEngine"/>.
/// </summary>
public static class StandardPartArchetypeRenderer
{
    public const byte DefaultBackgroundIntensity = 232;
    private const byte DefaultForegroundIntensity = 28;
    private const float DegreesToRadians = MathF.PI / 180.0f;
    private const int DefaultHalfExtentPixels = 135;
    private const float PixelCenterOffset = 0.5f;
    private const float AntiAliasSdfBandPixels = 3.0f;
    private const float ErfBlurScale = 0.85f;
    // [Math Why]: Abramowitz & Stegun 7.1.26 rational polynomial approximation constants for erf(x) (max error <= 5e-4).
    private const float ErfApproxA1 = 0.278393f;
    private const float ErfApproxA2 = 0.230389f;
    private const float ErfApproxA3 = 0.000972f;
    private const float ErfApproxA4 = 0.078108f;
    private const int MaxByteIntensity = byte.MaxValue;

    /// <summary>
    /// Renders one of the 3 standard part archetypes (1 = Bracket, 2 = Connector, 3 = ShieldCan)
    /// into an 8-bit grayscale buffer at the specified center and rotation angle.
    /// </summary>
    public static void RenderPartIntoBuffer(
        byte[] buffer,
        int width,
        int height,
        int stride,
        int partType,
        float centerX,
        float centerY,
        float angleDeg)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, width);

        float angleRad = angleDeg * DegreesToRadians;
        float cos = MathF.Cos(angleRad);
        float sin = MathF.Sin(angleRad);

        int minX = Math.Max(1, (int)MathF.Floor(centerX - DefaultHalfExtentPixels));
        int maxX = Math.Min(width - 2, (int)MathF.Ceiling(centerX + DefaultHalfExtentPixels));
        int minY = Math.Max(1, (int)MathF.Floor(centerY - DefaultHalfExtentPixels));
        int maxY = Math.Min(height - 2, (int)MathF.Ceiling(centerY + DefaultHalfExtentPixels));

        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                float dx = (x + PixelCenterOffset) - centerX;
                float dy = (y + PixelCenterOffset) - centerY;
                float lx = (dx * cos) + (dy * sin);
                float ly = (-dx * sin) + (dy * cos);

                float sdf = EvaluatePartSdf(partType, lx, ly);
                if (sdf < AntiAliasSdfBandPixels)
                {
                    float alpha = PixelCenterOffset * (1.0f - ErfApprox(sdf * ErfBlurScale));
                    byte cur = buffer[(y * stride) + x];
                    int blended = (int)MathF.Round(cur + ((DefaultForegroundIntensity - cur) * alpha));
                    buffer[(y * stride) + x] = (byte)Math.Clamp(blended, 0, MaxByteIntensity);
                }
            }
        }
    }

    private static float EvaluatePartSdf(int partType, float lx, float ly)
    {
        if (partType == 1)
        {
            float r = MathF.Sqrt((lx * lx) + (ly * ly));
            float outerRing = MathF.Abs(r - 58.0f) - 6.0f;
            float innerRing = MathF.Abs(r - 26.0f) - 5.0f;
            float keywayTab = SdfBox(lx - 42.0f, ly, 18.0f, 8.0f);
            float lCornerH = SdfBox(lx + 45.0f, ly + 74.0f, 28.0f, 4.5f);
            float lCornerV = SdfBox(lx + 70.0f, ly + 50.0f, 4.5f, 28.0f);
            return MathF.Min(MathF.Min(outerRing, innerRing), MathF.Min(keywayTab, MathF.Min(lCornerH, lCornerV)));
        }

        if (partType == 2)
        {
            float boxFrame = MathF.Abs(SdfBox(lx, ly, 62.0f, 42.0f)) - 4.5f;
            float pin1Dot = MathF.Sqrt(((lx + 38.0f) * (lx + 38.0f)) + ((ly + 20.0f) * (ly + 20.0f))) - 8.5f;
            float pins = 1e6f;
            for (int i = -2; i <= 2; i++)
            {
                if (i == 0)
                {
                    continue;
                }

                float px = i * 22.0f;
                pins = MathF.Min(pins, SdfBox(lx - px, ly + 56.0f, 4.5f, 12.0f));
                pins = MathF.Min(pins, SdfBox(lx - px, ly - 56.0f, 4.5f, 12.0f));
            }

            return MathF.Min(MathF.Min(boxFrame, pin1Dot), pins);
        }

        float crossH = SdfBox(lx, ly, 52.0f, 11.0f);
        float crossV = SdfBox(lx, ly, 11.0f, 52.0f);
        float cross = MathF.Min(crossH, crossV);
        float orientDot = MathF.Sqrt(((lx - 36.0f) * (lx - 36.0f)) + ((ly + 36.0f) * (ly + 36.0f))) - 7.0f;
        float outerOct = MathF.Abs(SdfBox(lx, ly, 76.0f, 76.0f)) - 3.5f;
        return MathF.Min(MathF.Min(cross, orientDot), outerOct);
    }

    private static float SdfBox(float x, float y, float halfW, float halfH)
    {
        float dx = MathF.Abs(x) - halfW;
        float dy = MathF.Abs(y) - halfH;
        float outside = MathF.Sqrt((MathF.Max(dx, 0f) * MathF.Max(dx, 0f)) + (MathF.Max(dy, 0f) * MathF.Max(dy, 0f)));
        float inside = MathF.Min(MathF.Max(dx, dy), 0f);
        return outside + inside;
    }

    private static float ErfApprox(float x)
    {
        float sign = x < 0f ? -1.0f : 1.0f;
        float ax = MathF.Abs(x);
        float denom = 1.0f + (ErfApproxA1 * ax) + (ErfApproxA2 * ax * ax) + (ErfApproxA3 * ax * ax * ax) + (ErfApproxA4 * ax * ax * ax * ax);
        float denom2 = denom * denom;
        float denom4 = denom2 * denom2;
        return sign * (1.0f - (1.0f / denom4));
    }
}
