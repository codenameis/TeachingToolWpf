using System.Numerics;
using System.Runtime.CompilerServices;

namespace VisionAlgorithm.OpenSrc.Calibration;

/// <summary>
/// Represents a calibrated $3 \times 3$ planar homography $H$ mapping image pixel coordinates $(u, v, 1)^T$
/// to physical motion stage coordinates $(X_{\text{mm}}, Y_{\text{mm}}, 1)^T$ and its inverse $H^{-1}$.
/// </summary>
public sealed class PlanarHomography2D
{
    private readonly double[] _hForward;
    private readonly double[] _hInverse;

    public double RmsReprojectionErrorMm { get; }
    public double MaxReprojectionErrorMm { get; }
    public ReadOnlySpan<double> ForwardMatrix => _hForward;
    public ReadOnlySpan<double> InverseMatrix => _hInverse;

    public PlanarHomography2D(double[] hForward, double[] hInverse, double rmsErrorMm, double maxErrorMm)
    {
        ArgumentNullException.ThrowIfNull(hForward);
        ArgumentNullException.ThrowIfNull(hInverse);
        if (hForward.Length != 9 || hInverse.Length != 9)
        {
            throw new ArgumentException("Homography matrices must contain 9 elements (3x3 row-major).");
        }

        _hForward = hForward;
        _hInverse = hInverse;
        RmsReprojectionErrorMm = rmsErrorMm;
        MaxReprojectionErrorMm = maxErrorMm;
    }

    /// <summary>Maps an image pixel coordinate $(u, v)$ to physical stage coordinates $(X_{\text{mm}}, Y_{\text{mm}})$.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Vector2 PixelToWorldMm(Vector2 pixelPoint)
    {
        double u = pixelPoint.X;
        double v = pixelPoint.Y;
        double w = (_hForward[6] * u) + (_hForward[7] * v) + _hForward[8];
        double invW = Math.Abs(w) > 1e-12 ? 1.0 / w : 1.0;

        double x = ((_hForward[0] * u) + (_hForward[1] * v) + _hForward[2]) * invW;
        double y = ((_hForward[3] * u) + (_hForward[4] * v) + _hForward[5]) * invW;
        return new Vector2((float)x, (float)y);
    }

    /// <summary>Maps a physical stage coordinate $(X_{\text{mm}}, Y_{\text{mm}})$ back to image pixel coordinates $(u, v)$.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Vector2 WorldMmToPixel(Vector2 worldMmPoint)
    {
        double x = worldMmPoint.X;
        double y = worldMmPoint.Y;
        double w = (_hInverse[6] * x) + (_hInverse[7] * y) + _hInverse[8];
        double invW = Math.Abs(w) > 1e-12 ? 1.0 / w : 1.0;

        double u = ((_hInverse[0] * x) + (_hInverse[1] * y) + _hInverse[2]) * invW;
        double v = ((_hInverse[3] * x) + (_hInverse[4] * y) + _hInverse[5]) * invW;
        return new Vector2((float)u, (float)v);
    }
}
