// File: src/VisionAlgorithm/OpenSrc/Metrology/TaubinCircleFitter.cs
using System.Numerics;
using System.Runtime.CompilerServices;
using VisionAlgorithm.OpenSrc.Common;

namespace VisionAlgorithm.OpenSrc.Metrology;

/// <summary>
/// Represents a subpixel fitted circle $(C_x, C_y, R)$ and its radial fit quality.
/// </summary>
public readonly record struct FittedCircle2D(
    bool IsValid,
    Vector2 Center,
    float Radius,
    float Diameter,
    float RmsRadialErrorPixels,
    int InlierCount);

/// <summary>
/// Implements Taubin (1991) gradient-weighted algebraic circle fitting ("Estimation of Planar Curves, Surfaces, and
/// Nonplanar Space Curves Defined by Implicit Equations with Applications to Edge and Range Image Segmentation",
/// IEEE TPAMI 13(11):1115-1138) followed by Gauss-Newton geometric distance refinement.
/// </summary>
public static class TaubinCircleFitter
{
    // [Math Why]: Minimum number of non-collinear points required to uniquely define a circle in R^2.
    private const int MinimumCirclePoints = 3;

    // [Perf Why]: 8 Newton-Raphson iterations solve Taubin's characteristic polynomial to machine precision (<1e-12).
    private const int CharacteristicNewtonIterations = 8;

    // [Perf Why]: 6 Gauss-Newton geometric refinement steps eliminate any algebraic curvature bias on partial arcs.
    private const int GeometricGaussNewtonIterations = 6;

    // [Math Why]: Singular determinant guard prevents division by zero on collinear point sets.
    private const double SingularDeterminantEpsilon = 1e-12;

    // [Perf Why]: Spoke counts up to 128 fit on the stack without heap allocation.
    private const int MaxStackAllocSpokes = 128;

    // [Math Why]: Orthogonal projection strip width (7 px) averages radial spoke intensity across local pixel jitter.
    private const float DefaultSpokeProjectionWidthPixels = 7f;

    // [Math Why]: Squared parameter update norm threshold (1e-10 px^2 -> 1e-5 px step) for Gauss-Newton convergence.
    private const double GaussNewtonConvergenceStepSqTol = 1e-10;

    // [Math Why]: Coefficient 4.0 in Taubin's characteristic equation and quadratic discriminant.
    private const double TaubinQuadraticFactorFour = 4.0;

    /// <summary>
    /// Fits a circle to <paramref name="points"/> using Taubin's algebraic initialization and Gauss-Newton geometric refinement.
    /// </summary>
    public static FittedCircle2D FitCircle(ReadOnlySpan<Vector2> points)
    {
        if (points.Length < MinimumCirclePoints)
        {
            return default;
        }

        int n = points.Length;
        double meanX = 0.0;
        double meanY = 0.0;
        for (int i = 0; i < n; i++)
        {
            meanX += points[i].X;
            meanY += points[i].Y;
        }

        double invN = 1.0 / n;
        meanX *= invN;
        meanY *= invN;

        // Compute centered moments (Chernov-Lesort formulation of Taubin's method)
        double mxx = 0.0, myy = 0.0, mxy = 0.0, mxz = 0.0, myz = 0.0, mzz = 0.0;
        for (int i = 0; i < n; i++)
        {
            double xi = points[i].X - meanX;
            double yi = points[i].Y - meanY;
            double zi = (xi * xi) + (yi * yi);

            mxx += xi * xi;
            myy += yi * yi;
            mxy += xi * yi;
            mxz += xi * zi;
            myz += yi * zi;
            mzz += zi * zi;
        }

        mxx *= invN;
        myy *= invN;
        mxy *= invN;
        mxz *= invN;
        myz *= invN;
        mzz *= invN;

        double mz = mxx + myy;
        double covXy = (mxx * myy) - (mxy * mxy);
        double varZ = mzz - (mz * mz);

        // [Math Why]: Taubin's depressed cubic characteristic polynomial P(eta) = A3*eta^3 + A2*eta^2 + A1*eta + A0 = 0
        // where the smallest non-negative root eta >= 0 minimizes ||nabla f||^{-2} ||f||^2, and A0 = -det(M).
        double a3 = TaubinQuadraticFactorFour * mz;
        double a2 = (-3.0 * mz * mz) - mzz;
        double a1 = (varZ * mz) + (TaubinQuadraticFactorFour * covXy * mz) - (mxz * mxz) - (myz * myz);
        double a0 = (mxz * ((myy * mxz) - (mxy * myz)))
            + (myz * ((mxx * myz) - (mxy * mxz)))
            - (varZ * covXy);

        double a22 = a2 + a2;
        double a33 = a3 * 3.0;

        double x = 0.0;
        double y = a0;
        for (int iter = 0; iter < CharacteristicNewtonIterations; iter++)
        {
            double dy = a1 + (x * (a22 + (x * a33)));
            if (Math.Abs(dy) < SingularDeterminantEpsilon)
            {
                break;
            }

            double xNew = Math.Max(0.0, x - (y / dy));
            double yNew = a0 + (xNew * (a1 + (xNew * (a2 + (xNew * a3)))));
            if (Math.Abs(xNew - x) < SingularDeterminantEpsilon)
            {
                x = xNew;
                break;
            }

            x = xNew;
            y = yNew;
        }

        double det = (x * x) - (x * mz) + covXy;
        if (Math.Abs(det) < SingularDeterminantEpsilon)
        {
            return default;
        }

        double centerXLocal = ((mxz * (myy - x)) - (myz * mxy)) / (2.0 * det);
        double centerYLocal = ((myz * (mxx - x)) - (mxz * mxy)) / (2.0 * det);

        double cx = centerXLocal + meanX;
        double cy = centerYLocal + meanY;
        double radius = Math.Sqrt((centerXLocal * centerXLocal) + (centerYLocal * centerYLocal) + mz);

        // Refine (cx, cy, radius) with geometric Gauss-Newton orthogonal distance minimization
        RefineGeometricGaussNewton(points, ref cx, ref cy, ref radius);

        if (!double.IsFinite(cx) || !double.IsFinite(cy) || !double.IsFinite(radius) || radius <= 0.0)
        {
            return default;
        }

        double sumSqErr = 0.0;
        for (int i = 0; i < n; i++)
        {
            double dx = points[i].X - cx;
            double dy = points[i].Y - cy;
            double rErr = Math.Sqrt((dx * dx) + (dy * dy)) - radius;
            sumSqErr += rErr * rErr;
        }

        float rmsError = (float)Math.Sqrt(sumSqErr * invN);
        return new FittedCircle2D(
            IsValid: true,
            Center: new Vector2((float)cx, (float)cy),
            Radius: (float)radius,
            Diameter: (float)(2.0 * radius),
            RmsRadialErrorPixels: rmsError,
            InlierCount: n);
    }

    /// <summary>
    /// Measures radial spoke edges around an expected circular boundary on <paramref name="imageView"/> and fits a subpixel circle.
    /// </summary>
    public static FittedCircle2D MeasureSpokeCircle(
        ReadOnlyImageView2D<byte> imageView,
        Vector2 nominalCenter,
        float nominalRadius,
        float radialScanLength,
        int spokeCount,
        CaliperEdgePolarity radialPolarity,
        CaliperMeasurementSpec caliperSpec,
        InspectionWorkspace workspace)
    {
        imageView.EnsureValid();
        caliperSpec.EnsureValid();
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentOutOfRangeException.ThrowIfLessThan(spokeCount, MinimumCirclePoints);

        Span<Vector2> edgePoints = spokeCount <= MaxStackAllocSpokes
            ? stackalloc Vector2[spokeCount]
            : new Vector2[spokeCount];

        int validCount = 0;
        float angleStep = (2f * MathF.PI) / spokeCount;

        for (int s = 0; s < spokeCount; s++)
        {
            float angle = s * angleStep;
            (float sinA, float cosA) = MathF.SinCos(angle);
            Vector2 spokeCenter = new(
                nominalCenter.X + (nominalRadius * cosA),
                nominalCenter.Y + (nominalRadius * sinA));

            FixturePose2D spokePose = new(spokeCenter.X, spokeCenter.Y, angle, 1f);
            if (SubpixelCaliper1D.TryMeasureSingleEdge(
                imageView,
                spokePose,
                radialScanLength,
                projectionWidthPixels: DefaultSpokeProjectionWidthPixels,
                desiredPolarity: radialPolarity,
                spec: caliperSpec,
                workspace: workspace,
                out CaliperEdgeHit hit))
            {
                edgePoints[validCount++] = hit.WorldPoint;
            }
        }

        return FitCircle(edgePoints.Slice(0, validCount));
    }

    /// <summary>
    /// Measures radial spoke edges around an expected circular boundary and fits a subpixel circle.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static FittedCircle2D MeasureSpokeCircle(
        ReadOnlySpan<byte> image,
        int width,
        int height,
        int stride,
        Vector2 nominalCenter,
        float nominalRadius,
        float radialScanLength,
        int spokeCount,
        CaliperEdgePolarity radialPolarity,
        CaliperMeasurementSpec caliperSpec,
        InspectionWorkspace workspace)
        => MeasureSpokeCircle(
            new ReadOnlyImageView2D<byte>(image, width, height, stride),
            nominalCenter,
            nominalRadius,
            radialScanLength,
            spokeCount,
            radialPolarity,
            caliperSpec,
            workspace);

    private static void RefineGeometricGaussNewton(
        ReadOnlySpan<Vector2> points,
        ref double cx,
        ref double cy,
        ref double radius)
    {
        int n = points.Length;
        double invN = 1.0 / n;

        for (int iter = 0; iter < GeometricGaussNewtonIterations; iter++)
        {
            double j11 = 0.0, j12 = 0.0, j22 = 0.0;
            double j13 = 0.0, j23 = 0.0;
            double g1 = 0.0, g2 = 0.0, g3 = 0.0;

            for (int i = 0; i < n; i++)
            {
                double dx = cx - points[i].X;
                double dy = cy - points[i].Y;
                double dist = Math.Sqrt((dx * dx) + (dy * dy));
                if (dist < SingularDeterminantEpsilon)
                {
                    continue;
                }

                double ux = dx / dist;
                double uy = dy / dist;
                double residual = dist - radius;

                j11 += ux * ux;
                j12 += ux * uy;
                j22 += uy * uy;
                j13 -= ux;
                j23 -= uy;

                g1 += ux * residual;
                g2 += uy * residual;
                g3 -= residual;
            }

            // Eliminate dR using third normal equation: n * dR + j13 * dCx + j23 * dCy = -g3
            double a11 = j11 - (j13 * j13 * invN);
            double a12 = j12 - (j13 * j23 * invN);
            double a22 = j22 - (j23 * j23 * invN);

            double b1 = -g1 + (j13 * g3 * invN);
            double b2 = -g2 + (j23 * g3 * invN);

            double det = (a11 * a22) - (a12 * a12);
            if (Math.Abs(det) < SingularDeterminantEpsilon)
            {
                break;
            }

            double dCx = ((a22 * b1) - (a12 * b2)) / det;
            double dCy = ((a11 * b2) - (a12 * b1)) / det;
            double dR = (-g3 - (j13 * dCx) - (j23 * dCy)) * invN;

            cx += dCx;
            cy += dCy;
            radius += dR;

            if ((dCx * dCx) + (dCy * dCy) + (dR * dR) < GaussNewtonConvergenceStepSqTol)
            {
                break;
            }
        }
    }
}
