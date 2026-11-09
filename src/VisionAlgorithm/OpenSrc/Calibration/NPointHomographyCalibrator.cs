// File: src/VisionAlgorithm/OpenSrc/Calibration/NPointHomographyCalibrator.cs
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

/// <summary>
/// Implements Hartley (1997) isotropic coordinate normalization ("In Defense of the Eight-Point Algorithm",
/// IEEE TPAMI 19(6):580-593) combined with Direct Linear Transformation (DLT) and Gauss-Newton geometric
/// reprojection refinement for 9-point (or $N$-point, $N \ge 4$) Hand-Eye pixel-to-stage calibration.
/// </summary>
public static class NPointHomographyCalibrator
{
    // [Math Why]: Minimum number of non-collinear 2D point correspondences required to determine an 8-DoF homography.
    private const int MinimumCorrespondences = 4;

    // [Math Why]: Hartley (1997) isotropic scaling sets average distance from centroid to sqrt(2) = 1.4142135623730951.
    private const double Sqrt2 = 1.4142135623730951;

    // [Math Why]: Pivot tolerance guards Gaussian elimination against singular configurations.
    private const double SingularPivotEpsilon = 1e-13;

    // [Perf Why]: 5 Gauss-Newton iterations achieve machine-precision convergence from the algebraic DLT initialization.
    private const int MaxGaussNewtonIterations = 5;

    // [Math Why]: Step norm convergence threshold in Hartley-normalized parameter space.
    private const double GaussNewtonStepToleranceSq = 1e-24;

    /// <summary>
    /// Calibrates a <see cref="PlanarHomography2D"/> from $N \ge 4$ point pairs (typically a $3 \times 3$ 9-point robot/stage calibration grid).
    /// </summary>
    public static PlanarHomography2D Calibrate(
        ReadOnlySpan<Vector2> pixelPoints,
        ReadOnlySpan<Vector2> worldMmPoints)
    {
        if (pixelPoints.Length < MinimumCorrespondences || worldMmPoints.Length != pixelPoints.Length)
        {
            throw new ArgumentException($"At least {MinimumCorrespondences} matching pixel and world point pairs are required.");
        }

        int n = pixelPoints.Length;
        Span<double> normSrcX = n <= 64 ? stackalloc double[n] : new double[n];
        Span<double> normSrcY = n <= 64 ? stackalloc double[n] : new double[n];
        Span<double> normDstX = n <= 64 ? stackalloc double[n] : new double[n];
        Span<double> normDstY = n <= 64 ? stackalloc double[n] : new double[n];

        ComputeHartleyNormalization(pixelPoints, normSrcX, normSrcY, out double srcMeanX, out double srcMeanY, out double srcScale);
        ComputeHartleyNormalization(worldMmPoints, normDstX, normDstY, out double dstMeanX, out double dstMeanY, out double dstScale);

        // Assemble 8x8 normal equations (A^T * A) * h = A^T * b with h_33 = 1 in normalized coordinates
        Span<double> ata = stackalloc double[64];
        Span<double> atb = stackalloc double[8];
        Span<double> row1 = stackalloc double[8];
        Span<double> row2 = stackalloc double[8];

        for (int i = 0; i < n; i++)
        {
            double x = normSrcX[i];
            double y = normSrcY[i];
            double xp = normDstX[i];
            double yp = normDstY[i];

            // Row 1: [x, y, 1, 0, 0, 0, -x*xp, -y*xp] * h = xp
            row1[0] = x;
            row1[1] = y;
            row1[2] = 1.0;
            row1[3] = 0.0;
            row1[4] = 0.0;
            row1[5] = 0.0;
            row1[6] = -x * xp;
            row1[7] = -y * xp;

            // Row 2: [0, 0, 0, x, y, 1, -x*yp, -y*yp] * h = yp
            row2[0] = 0.0;
            row2[1] = 0.0;
            row2[2] = 0.0;
            row2[3] = x;
            row2[4] = y;
            row2[5] = 1.0;
            row2[6] = -x * yp;
            row2[7] = -y * yp;

            AccumulateNormalEquation(ata, atb, row1, xp);
            AccumulateNormalEquation(ata, atb, row2, yp);
        }

        double[] hNorm = new double[9];
        if (!VisionAlgorithm.OpenSrc.Common.SmallMatrixSolver.TrySolveLinearSystem(ata, atb, 8, hNorm.AsSpan(0, 8)))
        {
            throw new InvalidOperationException("Point configuration is degenerate (collinear) and cannot determine a unique 2D homography.");
        }
        hNorm[8] = 1.0;

        RefineHomographyGaussNewton(normSrcX, normSrcY, normDstX, normDstY, hNorm);

        // Denormalize: H = T_dst^-1 * H_norm * T_src
        double[] hForward = DenormalizeHomography(hNorm, srcMeanX, srcMeanY, srcScale, dstMeanX, dstMeanY, dstScale);
        double[] hInverse = Invert3x3(hForward);

        double sumSqErr = 0.0;
        double maxErr = 0.0;
        PlanarHomography2D tempHomography = new(hForward, hInverse, 0.0, 0.0);

        for (int i = 0; i < n; i++)
        {
            Vector2 predicted = tempHomography.PixelToWorldMm(pixelPoints[i]);
            double dx = predicted.X - worldMmPoints[i].X;
            double dy = predicted.Y - worldMmPoints[i].Y;
            double errSq = (dx * dx) + (dy * dy);
            sumSqErr += errSq;
            double err = Math.Sqrt(errSq);
            if (err > maxErr)
            {
                maxErr = err;
            }
        }

        double rmsErr = Math.Sqrt(sumSqErr / n);
        return new PlanarHomography2D(hForward, hInverse, rmsErr, maxErr);
    }

    private static void RefineHomographyGaussNewton(
        ReadOnlySpan<double> normSrcX,
        ReadOnlySpan<double> normSrcY,
        ReadOnlySpan<double> normDstX,
        ReadOnlySpan<double> normDstY,
        Span<double> hNorm)
    {
        int n = normSrcX.Length;
        Span<double> jtj = stackalloc double[64];
        Span<double> negJtr = stackalloc double[8];
        Span<double> jx = stackalloc double[8];
        Span<double> jy = stackalloc double[8];
        Span<double> deltaH = stackalloc double[8];
        Span<double> hCandidate = stackalloc double[9];
        hCandidate[8] = 1.0;

        double currentErrorSq = EvaluateNormalizedReprojectionErrorSq(normSrcX, normSrcY, normDstX, normDstY, hNorm);

        for (int iter = 0; iter < MaxGaussNewtonIterations; iter++)
        {
            jtj.Clear();
            negJtr.Clear();

            for (int i = 0; i < n; i++)
            {
                double u = normSrcX[i];
                double v = normSrcY[i];
                double dstX = normDstX[i];
                double dstY = normDstY[i];

                double denom = (hNorm[6] * u) + (hNorm[7] * v) + 1.0;
                if (Math.Abs(denom) <= SingularPivotEpsilon)
                {
                    return;
                }

                double invDenom = 1.0 / denom;
                double predX = ((hNorm[0] * u) + (hNorm[1] * v) + hNorm[2]) * invDenom;
                double predY = ((hNorm[3] * u) + (hNorm[4] * v) + hNorm[5]) * invDenom;

                double rx = predX - dstX;
                double ry = predY - dstY;

                // Jacobian row for r_x = (h0*u + h1*v + h2) / D - X
                jx[0] = u * invDenom;
                jx[1] = v * invDenom;
                jx[2] = invDenom;
                jx[3] = 0.0;
                jx[4] = 0.0;
                jx[5] = 0.0;
                jx[6] = -u * predX * invDenom;
                jx[7] = -v * predX * invDenom;

                // Jacobian row for r_y = (h3*u + h4*v + h5) / D - Y
                jy[0] = 0.0;
                jy[1] = 0.0;
                jy[2] = 0.0;
                jy[3] = u * invDenom;
                jy[4] = v * invDenom;
                jy[5] = invDenom;
                jy[6] = -u * predY * invDenom;
                jy[7] = -v * predY * invDenom;

                AccumulateNormalEquation(jtj, negJtr, jx, -rx);
                AccumulateNormalEquation(jtj, negJtr, jy, -ry);
            }

            if (!VisionAlgorithm.OpenSrc.Common.SmallMatrixSolver.TrySolveLinearSystem(jtj, negJtr, 8, deltaH))
            {
                break;
            }

            double stepNormSq = 0.0;
            for (int k = 0; k < 8; k++)
            {
                stepNormSq += deltaH[k] * deltaH[k];
                hCandidate[k] = hNorm[k] + deltaH[k];
            }

            double candidateErrorSq = EvaluateNormalizedReprojectionErrorSq(normSrcX, normSrcY, normDstX, normDstY, hCandidate);
            if (candidateErrorSq < currentErrorSq)
            {
                for (int k = 0; k < 8; k++)
                {
                    hNorm[k] = hCandidate[k];
                }
                currentErrorSq = candidateErrorSq;
            }
            else
            {
                break;
            }

            if (stepNormSq < GaussNewtonStepToleranceSq)
            {
                break;
            }
        }
    }

    private static double EvaluateNormalizedReprojectionErrorSq(
        ReadOnlySpan<double> normSrcX,
        ReadOnlySpan<double> normSrcY,
        ReadOnlySpan<double> normDstX,
        ReadOnlySpan<double> normDstY,
        ReadOnlySpan<double> h)
    {
        int n = normSrcX.Length;
        double sumErrorSq = 0.0;
        for (int i = 0; i < n; i++)
        {
            double u = normSrcX[i];
            double v = normSrcY[i];
            double denom = (h[6] * u) + (h[7] * v) + 1.0;
            if (Math.Abs(denom) <= SingularPivotEpsilon)
            {
                return double.PositiveInfinity;
            }

            double invDenom = 1.0 / denom;
            double rx = (((h[0] * u) + (h[1] * v) + h[2]) * invDenom) - normDstX[i];
            double ry = (((h[3] * u) + (h[4] * v) + h[5]) * invDenom) - normDstY[i];
            sumErrorSq += (rx * rx) + (ry * ry);
        }

        return sumErrorSq;
    }

    private static void ComputeHartleyNormalization(
        ReadOnlySpan<Vector2> points,
        Span<double> normX,
        Span<double> normY,
        out double meanX,
        out double meanY,
        out double scale)
    {
        int n = points.Length;
        meanX = 0.0;
        meanY = 0.0;
        for (int i = 0; i < n; i++)
        {
            meanX += points[i].X;
            meanY += points[i].Y;
        }

        meanX /= n;
        meanY /= n;

        double sumDist = 0.0;
        for (int i = 0; i < n; i++)
        {
            double dx = points[i].X - meanX;
            double dy = points[i].Y - meanY;
            sumDist += Math.Sqrt((dx * dx) + (dy * dy));
        }

        double meanDist = sumDist / n;
        scale = meanDist > SingularPivotEpsilon ? Sqrt2 / meanDist : 1.0;

        for (int i = 0; i < n; i++)
        {
            normX[i] = (points[i].X - meanX) * scale;
            normY[i] = (points[i].Y - meanY) * scale;
        }
    }

    private static void AccumulateNormalEquation(Span<double> ata, Span<double> atb, ReadOnlySpan<double> row, double rhs)
    {
        for (int r = 0; r < 8; r++)
        {
            double vr = row[r];
            atb[r] += vr * rhs;
            int rowOffset = r * 8;
            for (int c = 0; c < 8; c++)
            {
                ata[rowOffset + c] += vr * row[c];
            }
        }
    }

    private static double[] DenormalizeHomography(
        ReadOnlySpan<double> hNorm,
        double srcMeanX,
        double srcMeanY,
        double srcScale,
        double dstMeanX,
        double dstMeanY,
        double dstScale)
    {
        double[] tSrc =
        [
            srcScale, 0.0, -srcScale * srcMeanX,
            0.0, srcScale, -srcScale * srcMeanY,
            0.0, 0.0, 1.0
        ];

        double invDstScale = 1.0 / dstScale;
        double[] tDstInv =
        [
            invDstScale, 0.0, dstMeanX,
            0.0, invDstScale, dstMeanY,
            0.0, 0.0, 1.0
        ];

        double[] temp = Multiply3x3(hNorm, tSrc);
        double[] h = Multiply3x3(tDstInv, temp);

        double norm = Math.Abs(h[8]) > SingularPivotEpsilon ? 1.0 / h[8] : 1.0;
        for (int i = 0; i < 9; i++)
        {
            h[i] *= norm;
        }

        return h;
    }

    private static double[] Multiply3x3(ReadOnlySpan<double> a, ReadOnlySpan<double> b)
    {
        double[] c = new double[9];
        for (int r = 0; r < 3; r++)
        {
            for (int col = 0; col < 3; col++)
            {
                c[(r * 3) + col] =
                    (a[(r * 3) + 0] * b[0 + col]) +
                    (a[(r * 3) + 1] * b[3 + col]) +
                    (a[(r * 3) + 2] * b[6 + col]);
            }
        }

        return c;
    }

    private static double[] Invert3x3(ReadOnlySpan<double> m)
    {
        double c00 = (m[4] * m[8]) - (m[5] * m[7]);
        double c01 = -((m[3] * m[8]) - (m[5] * m[6]));
        double c02 = (m[3] * m[7]) - (m[4] * m[6]);

        double c10 = -((m[1] * m[8]) - (m[2] * m[7]));
        double c11 = (m[0] * m[8]) - (m[2] * m[6]);
        double c12 = -((m[0] * m[7]) - (m[1] * m[6]));

        double c20 = (m[1] * m[5]) - (m[2] * m[4]);
        double c21 = -((m[0] * m[5]) - (m[2] * m[3]));
        double c22 = (m[0] * m[4]) - (m[1] * m[3]);

        double det = (m[0] * c00) + (m[1] * c01) + (m[2] * c02);
        double invDet = Math.Abs(det) > SingularPivotEpsilon ? 1.0 / det : 1.0;

        return
        [
            c00 * invDet, c10 * invDet, c20 * invDet,
            c01 * invDet, c11 * invDet, c21 * invDet,
            c02 * invDet, c12 * invDet, c22 * invDet
        ];
    }
}
