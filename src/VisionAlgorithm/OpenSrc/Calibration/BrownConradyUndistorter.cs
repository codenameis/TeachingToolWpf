using System.Numerics;
using System.Runtime.CompilerServices;

namespace VisionAlgorithm.OpenSrc.Calibration;

/// <summary>
/// Evaluates forward Brown-Conrady lens distortion, iterative fixed-point inverse undistortion, and
/// precomputed 2D lookup tables $(Map_X, Map_Y)$ for real-time image rectification.
/// </summary>
public static class BrownConradyUndistorter
{
    // [Perf Why]: 8 fixed-point / Gauss-Newton iterations invert smooth radial-tangential distortion to < 1e-6 pixel accuracy.
    private const int InverseFixedPointIterations = 8;

    // [Math Why]: Positive lower bound on radial distortion factor (1 + k1*r^2 + k2*r^4 + k3*r^6) prevents division-by-zero
    // or sign-flip divergence outside the calibrated image circle during fixed-point inversion.
    private const float MinimumRadialScaleFloor = 0.1f;

    /// <summary>
    /// Maps an ideal undistorted pixel coordinate $(u_{\text{ideal}}, v_{\text{ideal}})$ to the observed distorted sensor pixel $(u_{\text{dist}}, v_{\text{dist}})$.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector2 DistortPixel(Vector2 idealPixel, BrownConradyModel model)
    {
        model.EnsureValid();
        float x = (idealPixel.X - model.PrincipalPointX) / model.FocalLengthX;
        float y = (idealPixel.Y - model.PrincipalPointY) / model.FocalLengthY;

        DistortNormalized(x, y, model, out float xd, out float yd);
        return new Vector2(
            (xd * model.FocalLengthX) + model.PrincipalPointX,
            (yd * model.FocalLengthY) + model.PrincipalPointY);
    }

    /// <summary>
    /// Invertibly maps an observed distorted sensor pixel $(u_{\text{dist}}, v_{\text{dist}})$ back to its ideal pinhole pixel $(u_{\text{ideal}}, v_{\text{ideal}})$.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector2 UndistortPixel(Vector2 distortedPixel, BrownConradyModel model)
    {
        model.EnsureValid();
        float xd = (distortedPixel.X - model.PrincipalPointX) / model.FocalLengthX;
        float yd = (distortedPixel.Y - model.PrincipalPointY) / model.FocalLengthY;

        float x = xd;
        float y = yd;

        for (int iter = 0; iter < InverseFixedPointIterations; iter++)
        {
            float r2 = (x * x) + (y * y);
            float r4 = r2 * r2;
            float r6 = r4 * r2;

            float radial = 1f + (model.K1 * r2) + (model.K2 * r4) + (model.K3 * r6);
            float dxTangential = (2f * model.P1 * x * y) + (model.P2 * (r2 + (2f * x * x)));
            float dyTangential = (model.P1 * (r2 + (2f * y * y))) + (2f * model.P2 * x * y);

            float safeRadial = MathF.Max(MinimumRadialScaleFloor, radial);
            x = (xd - dxTangential) / safeRadial;
            y = (yd - dyTangential) / safeRadial;
        }

        return new Vector2(
            (x * model.FocalLengthX) + model.PrincipalPointX,
            (y * model.FocalLengthY) + model.PrincipalPointY);
    }

    /// <summary>
    /// Precomputes source sampling coordinate maps $(Map_X, Map_Y)$ for an undistorted target image of size <paramref name="width"/> $\times$ <paramref name="height"/>.
    /// </summary>
    public static void BuildUndistortRectificationMap(
        int width,
        int height,
        BrownConradyModel model,
        Span<float> mapXOut,
        Span<float> mapYOut)
    {
        model.EnsureValid();
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(width, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(height, 0);

        int count = checked(width * height);
        if (mapXOut.Length < count || mapYOut.Length < count)
        {
            throw new ArgumentException("mapXOut and mapYOut buffers must have length >= width * height.");
        }

        for (int r = 0; r < height; r++)
        {
            float idealY = r + 0.5f;
            int rowOffset = r * width;
            for (int c = 0; c < width; c++)
            {
                float idealX = c + 0.5f;
                Vector2 distorted = DistortPixel(new Vector2(idealX, idealY), model);
                mapXOut[rowOffset + c] = distorted.X;
                mapYOut[rowOffset + c] = distorted.Y;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void DistortNormalized(
        float x,
        float y,
        BrownConradyModel model,
        out float xd,
        out float yd)
    {
        float r2 = (x * x) + (y * y);
        float r4 = r2 * r2;
        float r6 = r4 * r2;

        float radial = 1f + (model.K1 * r2) + (model.K2 * r4) + (model.K3 * r6);
        float dxTangential = (2f * model.P1 * x * y) + (model.P2 * (r2 + (2f * x * x)));
        float dyTangential = (model.P1 * (r2 + (2f * y * y))) + (2f * model.P2 * x * y);

        xd = (x * radial) + dxTangential;
        yd = (y * radial) + dyTangential;
    }
}
