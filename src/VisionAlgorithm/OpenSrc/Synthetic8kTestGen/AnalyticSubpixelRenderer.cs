// File: src/VisionAlgorithm/OpenSrc/Synthetic8kTestGen/AnalyticSubpixelRenderer.cs
using System.Numerics;
using System.Runtime.CompilerServices;

namespace VisionAlgorithm.OpenSrc.Synthetic8kTestGen;

/// <summary>
/// Renders subpixel-accurate geometric primitives and curvilinear paths into an 8-bit grayscale buffer
/// using analytical Signed Distance Fields (SDF) convolved with a Gaussian Point Spread Function (PSF) via the error function $\operatorname{erf}(z)$.
/// </summary>
public static class AnalyticSubpixelRenderer
{
    // [Math Why]: Factor 1 / sqrt(2) = 0.70710678f scales signed distance d / sigma into erf(d / (sqrt(2) * sigma)).
    private const float InvSqrt2 = 0.70710678f;

    // [Math Why]: Abramowitz & Stegun 7.1.26 rational approximation constants for erf(x) with max error <= 1.5e-7.
    private const float ErfP = 0.3275911f;
    private const float ErfA1 = 0.254829592f;
    private const float ErfA2 = -0.284496736f;
    private const float ErfA3 = 1.421413741f;
    private const float ErfA4 = -1.453152027f;
    private const float ErfA5 = 1.061405429f;

    // [Math Why]: Outside +/- 4 * sigma, the Gaussian PSF transition is saturated to 0.0 or 1.0 within < 6e-5.
    private const float PsfTransitionCutoffMultiplier = 4.0f;

    /// <summary>
    /// Evaluates the smooth Gaussian PSF coverage $\Phi(-d / \sigma_{\text{psf}}) = \frac{1}{2}\left(1 - \operatorname{erf}\!\left(\frac{d}{\sqrt{2}\sigma_{\text{psf}}}\right)\right) \in [0, 1]$
    /// for a signed distance $d$ ($d < 0$ inside the shape, $d > 0$ outside).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float EvaluateGaussianEdgeCoverage(float signedDistance, float psfSigma = 0.65f)
    {
        float cutoff = psfSigma * PsfTransitionCutoffMultiplier;
        if (signedDistance <= -cutoff)
        {
            return 1f;
        }

        if (signedDistance >= cutoff)
        {
            return 0f;
        }

        float z = signedDistance * InvSqrt2 / MathF.Max(0.1f, psfSigma);
        return 0.5f * (1f - FastErf(z));
    }

    /// <summary>
    /// Renders an anti-aliased oriented rectangle (or trace/pad) centered at <paramref name="centerWorld"/>
    /// with subpixel dimensions <paramref name="widthPixels"/> $\times$ <paramref name="heightPixels"/> and rotation <paramref name="angleRad"/>.
    /// </summary>
    public static void RenderOrientedBox(
        Span<byte> canvas,
        int canvasWidth,
        int canvasHeight,
        int canvasStride,
        Vector2 centerWorld,
        float widthPixels,
        float heightPixels,
        float angleRad,
        float foregroundIntensity,
        float psfSigma = 0.65f)
    {
        float halfW = widthPixels * 0.5f;
        float halfH = heightPixels * 0.5f;
        (float sinA, float cosA) = MathF.SinCos(angleRad);

        float boundRadius = MathF.Sqrt((halfW * halfW) + (halfH * halfH)) + (psfSigma * PsfTransitionCutoffMultiplier) + 2f;
        int minX = Math.Max(0, (int)MathF.Floor(centerWorld.X - boundRadius));
        int maxX = Math.Min(canvasWidth - 1, (int)MathF.Ceiling(centerWorld.X + boundRadius));
        int minY = Math.Max(0, (int)MathF.Floor(centerWorld.Y - boundRadius));
        int maxY = Math.Min(canvasHeight - 1, (int)MathF.Ceiling(centerWorld.Y + boundRadius));

        for (int r = minY; r <= maxY; r++)
        {
            float py = (r + 0.5f) - centerWorld.Y;
            int rowOffset = r * canvasStride;
            for (int c = minX; c <= maxX; c++)
            {
                float px = (c + 0.5f) - centerWorld.X;

                // Rotate world offset (px, py) into box-local coordinates
                float lx = MathF.Abs((px * cosA) + (py * sinA)) - halfW;
                float ly = MathF.Abs((-px * sinA) + (py * cosA)) - halfH;

                float outX = MathF.Max(lx, 0f);
                float outY = MathF.Max(ly, 0f);
                float signedDist = MathF.Sqrt((outX * outX) + (outY * outY)) + MathF.Min(MathF.Max(lx, ly), 0f);

                float alpha = EvaluateGaussianEdgeCoverage(signedDist, psfSigma);
                if (alpha > 0f)
                {
                    float bg = canvas[rowOffset + c];
                    int blended = (int)MathF.Round(bg + (alpha * (foregroundIntensity - bg)));
                    canvas[rowOffset + c] = (byte)Math.Clamp(blended, byte.MinValue, byte.MaxValue);
                }
            }
        }
    }

    /// <summary>
    /// Renders an anti-aliased subpixel circle or annulus into <paramref name="canvas"/>.
    /// </summary>
    public static void RenderCircle(
        Span<byte> canvas,
        int canvasWidth,
        int canvasHeight,
        int canvasStride,
        Vector2 centerWorld,
        float radiusPixels,
        float foregroundIntensity,
        float psfSigma = 0.65f)
    {
        float pad = (psfSigma * PsfTransitionCutoffMultiplier) + 2f;
        int minX = Math.Max(0, (int)MathF.Floor(centerWorld.X - radiusPixels - pad));
        int maxX = Math.Min(canvasWidth - 1, (int)MathF.Ceiling(centerWorld.X + radiusPixels + pad));
        int minY = Math.Max(0, (int)MathF.Floor(centerWorld.Y - radiusPixels - pad));
        int maxY = Math.Min(canvasHeight - 1, (int)MathF.Ceiling(centerWorld.Y + radiusPixels + pad));

        for (int r = minY; r <= maxY; r++)
        {
            float dy = (r + 0.5f) - centerWorld.Y;
            int rowOffset = r * canvasStride;
            for (int c = minX; c <= maxX; c++)
            {
                float dx = (c + 0.5f) - centerWorld.X;
                float signedDist = MathF.Sqrt((dx * dx) + (dy * dy)) - radiusPixels;
                float alpha = EvaluateGaussianEdgeCoverage(signedDist, psfSigma);
                if (alpha > 0f)
                {
                    float bg = canvas[rowOffset + c];
                    int blended = (int)MathF.Round(bg + (alpha * (foregroundIntensity - bg)));
                    canvas[rowOffset + c] = (byte)Math.Clamp(blended, byte.MinValue, byte.MaxValue);
                }
            }
        }
    }

    /// <summary>
    /// Renders a capsule/segment of variable thickness between <paramref name="p0"/> and <paramref name="p1"/>
    /// with Gaussian cross-sectional or flat-top PSF profile.
    /// </summary>
    public static void RenderCapsuleSegment(
        Span<byte> canvas,
        int canvasWidth,
        int canvasHeight,
        int canvasStride,
        Vector2 p0,
        Vector2 p1,
        float widthPixels,
        float foregroundIntensity,
        float psfSigma = 0.65f)
    {
        float halfWidth = widthPixels * 0.5f;
        float pad = halfWidth + (psfSigma * PsfTransitionCutoffMultiplier) + 2f;

        int minX = Math.Max(0, (int)MathF.Floor(MathF.Min(p0.X, p1.X) - pad));
        int maxX = Math.Min(canvasWidth - 1, (int)MathF.Ceiling(MathF.Max(p0.X, p1.X) + pad));
        int minY = Math.Max(0, (int)MathF.Floor(MathF.Min(p0.Y, p1.Y) - pad));
        int maxY = Math.Min(canvasHeight - 1, (int)MathF.Ceiling(MathF.Max(p0.Y, p1.Y) + pad));

        Vector2 ab = p1 - p0;
        float abLenSq = MathF.Max(1e-6f, ab.LengthSquared());

        for (int r = minY; r <= maxY; r++)
        {
            float py = r + 0.5f;
            int rowOffset = r * canvasStride;
            for (int c = minX; c <= maxX; c++)
            {
                Vector2 p = new(c + 0.5f, py);
                float t = Math.Clamp(Vector2.Dot(p - p0, ab) / abLenSq, 0f, 1f);
                Vector2 closest = p0 + (ab * t);
                float signedDist = Vector2.Distance(p, closest) - halfWidth;

                float alpha = EvaluateGaussianEdgeCoverage(signedDist, psfSigma);
                if (alpha > 0f)
                {
                    float bg = canvas[rowOffset + c];
                    int blended = (int)MathF.Round(bg + (alpha * (foregroundIntensity - bg)));
                    canvas[rowOffset + c] = (byte)Math.Clamp(blended, byte.MinValue, byte.MaxValue);
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float FastErf(float x)
    {
        float sign = x < 0f ? -1f : 1f;
        float absX = MathF.Abs(x);
        float t = 1f / (1f + (ErfP * absX));
        float poly = ((((((((ErfA5 * t) + ErfA4) * t) + ErfA3) * t) + ErfA2) * t) + ErfA1) * t;
        float y = 1f - (poly * MathF.Exp(-(absX * absX)));
        return sign * y;
    }
}
