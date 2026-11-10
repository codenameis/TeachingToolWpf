// File: src/VisionAlgorithm/OpenSrc/Metrology/FitzgibbonEllipseFitter.cs
using System.Numerics;
using System.Runtime.CompilerServices;
using VisionAlgorithm.OpenSrc.Common;

namespace VisionAlgorithm.OpenSrc.Metrology;

/// <summary>
/// Implements Hartley-normalized direct algebraic conic least squares (Fitzgibbon, Pilu &amp; Fisher 1999)
/// followed by Sampson/Gauss-Newton orthogonal geometric distance refinement and IRLS Tukey biweight outlier rejection
/// (HALCON <c>fit_ellipse_contour_xld</c> / Cognex <c>CogFindEllipseTool</c> parity).
/// </summary>
public static class FitzgibbonEllipseFitter
{
    private const int MinimumEllipsePoints = 5;
    private const int AlgebraicBasisCount = 5;
    private const int NormalMatrixSize = AlgebraicBasisCount * AlgebraicBasisCount;
    private const int IrlsIterations = 5;
    private const int GaussNewtonIterations = 8;
    private const double TikhonovRegularization = 1e-10;
    private const double SingularDeterminantEpsilon = 1e-12;
    private const double RootTwo = 1.4142135623730951;
    private const double HalfFactor = 0.5;
    private const double TwoFactor = 2.0;
    private const double FourFactor = 4.0;
    private const double TukeyTuningConstant = 4.685;
    private const double MadToSigmaFactor = 1.4826;
    private const double MinimumRobustScalePixels = 0.25;
    private const double GaussNewtonStepConvergenceSq = 1e-10;
    private const int MaxStackAllocPoints = 256;
    private const float DefaultSpokeProjectionWidthPixels = 7.0f;

    /// <summary>
    /// Fits a 2D ellipse $(C_x, C_y, A, B, \theta)$ to <paramref name="points"/> using Hartley normalization,
    /// IRLS Tukey biweight conic least squares, and geometric Sampson/Gauss-Newton refinement.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static FittedEllipse2D FitEllipse(ReadOnlySpan<Vector2> points, float outlierDistanceThresholdPixels = 2.5f)
    {
        if (points.Length < MinimumEllipsePoints)
        {
            return default;
        }

        int n = points.Length;
        Span<double> weights = n <= MaxStackAllocPoints ? stackalloc double[n] : new double[n];
        Span<double> residuals = n <= MaxStackAllocPoints ? stackalloc double[n] : new double[n];
        weights.Fill(1.0);

        double cx = 0.0;
        double cy = 0.0;
        double aAxis = 0.0;
        double bAxis = 0.0;
        double angleRad = 0.0;
        bool hasValidModel = false;

        for (int irls = 0; irls < IrlsIterations; irls++)
        {
            if (!TrySolveWeightedAlgebraicEllipse(points, weights, out double curCx, out double curCy, out double curA, out double curB, out double curAngle))
            {
                break;
            }

            cx = curCx;
            cy = curCy;
            aAxis = curA;
            bAxis = curB;
            angleRad = curAngle;
            hasValidModel = true;

            for (int i = 0; i < n; i++)
            {
                residuals[i] = Math.Abs(ComputeSignedSampsonResidual(points[i].X, points[i].Y, cx, cy, aAxis, bAxis, angleRad));
            }

            double sigma = EstimateRobustSigma(residuals, n);
            double cutoff = Math.Max(MinimumRobustScalePixels, Math.Min(outlierDistanceThresholdPixels * 1.5, TukeyTuningConstant * sigma));
            double invCutoff = 1.0 / cutoff;

            int activeCount = 0;
            for (int i = 0; i < n; i++)
            {
                double u = residuals[i] * invCutoff;
                if (u < 1.0)
                {
                    double oneMinusUSq = 1.0 - (u * u);
                    weights[i] = oneMinusUSq * oneMinusUSq;
                    activeCount++;
                }
                else
                {
                    weights[i] = 0.0;
                }
            }

            if (activeCount < MinimumEllipsePoints)
            {
                weights.Fill(1.0);
                break;
            }
        }

        if (!hasValidModel)
        {
            return default;
        }

        // Refine ellipse parameters using geometric Sampson Gauss-Newton over inlier weights
        RefineEllipseSampsonGaussNewton(points, weights, ref cx, ref cy, ref aAxis, ref bAxis, ref angleRad);

        NormalizeCanonicalAxesAndAngle(ref aAxis, ref bAxis, ref angleRad);
        if (!double.IsFinite(cx) || !double.IsFinite(cy) || !double.IsFinite(aAxis) || !double.IsFinite(bAxis) || bAxis <= 0.0)
        {
            return default;
        }

        double sumSqErr = 0.0;
        int inlierCount = 0;
        double inlierThreshold = Math.Max(MinimumRobustScalePixels, outlierDistanceThresholdPixels);

        for (int i = 0; i < n; i++)
        {
            double sampsonErr = Math.Abs(ComputeSignedSampsonResidual(points[i].X, points[i].Y, cx, cy, aAxis, bAxis, angleRad));
            if (sampsonErr <= inlierThreshold)
            {
                sumSqErr += sampsonErr * sampsonErr;
                inlierCount++;
            }
        }

        if (inlierCount < MinimumEllipsePoints)
        {
            sumSqErr = 0.0;
            for (int i = 0; i < n; i++)
            {
                double sampsonErr = ComputeSignedSampsonResidual(points[i].X, points[i].Y, cx, cy, aAxis, bAxis, angleRad);
                sumSqErr += sampsonErr * sampsonErr;
            }
            inlierCount = n;
        }

        float rmsError = (float)Math.Sqrt(sumSqErr / inlierCount);
        return new FittedEllipse2D(
            IsValid: true,
            Center: new Vector2((float)cx, (float)cy),
            SemiMajorAxis: (float)aAxis,
            SemiMinorAxis: (float)bAxis,
            AngleRad: (float)angleRad,
            RmsGeometricErrorPixels: rmsError,
            InlierCount: inlierCount);
    }

    /// <summary>
    /// Measures radial/elliptical spoke subpixel edges around a nominal ellipse and fits a subpixel <see cref="FittedEllipse2D"/>.
    /// </summary>
    public static FittedEllipse2D MeasureSpokeEllipse(
        ReadOnlyImageView2D<byte> imageView,
        Vector2 nominalCenter,
        float nominalSemiMajor,
        float nominalSemiMinor,
        float nominalAngleRad,
        float radialScanLength,
        int spokeCount,
        CaliperEdgePolarity radialPolarity,
        CaliperMeasurementSpec caliperSpec,
        InspectionWorkspace workspace)
    {
        imageView.EnsureValid();
        caliperSpec.EnsureValid();
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentOutOfRangeException.ThrowIfLessThan(spokeCount, MinimumEllipsePoints);

        Span<Vector2> edgePoints = spokeCount <= MaxStackAllocPoints
            ? stackalloc Vector2[spokeCount]
            : new Vector2[spokeCount];

        int validCount = 0;
        float stepAngle = (2.0f * MathF.PI) / spokeCount;
        (float sinRot, float cosRot) = MathF.SinCos(nominalAngleRad);

        for (int s = 0; s < spokeCount; s++)
        {
            float t = s * stepAngle;
            (float sinT, float cosT) = MathF.SinCos(t);

            // Point on nominal ellipse in local frame: (a cos t, b sin t)
            float localX = nominalSemiMajor * cosT;
            float localY = nominalSemiMinor * sinT;
            Vector2 boundaryPt = new(
                nominalCenter.X + (localX * cosRot) - (localY * sinRot),
                nominalCenter.Y + (localX * sinRot) + (localY * cosRot));

            // Outward normal direction of ellipse in local frame: (b cos t, a sin t)
            float normalLocalX = nominalSemiMinor * cosT;
            float normalLocalY = nominalSemiMajor * sinT;
            float normalWorldX = (normalLocalX * cosRot) - (normalLocalY * sinRot);
            float normalWorldY = (normalLocalX * sinRot) + (normalLocalY * cosRot);
            float spokeAngle = MathF.Atan2(normalWorldY, normalWorldX);

            FixturePose2D spokePose = new(boundaryPt.X, boundaryPt.Y, spokeAngle, 1.0f);
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

        return FitEllipse(edgePoints.Slice(0, validCount));
    }

    private static bool TrySolveWeightedAlgebraicEllipse(
        ReadOnlySpan<Vector2> points,
        ReadOnlySpan<double> weights,
        out double cx,
        out double cy,
        out double semiMajor,
        out double semiMinor,
        out double angleRad)
    {
        cx = 0.0;
        cy = 0.0;
        semiMajor = 0.0;
        semiMinor = 0.0;
        angleRad = 0.0;

        int n = points.Length;
        double wSum = 0.0;
        double meanX = 0.0;
        double meanY = 0.0;

        for (int i = 0; i < n; i++)
        {
            double w = weights[i];
            if (w <= 0.0)
            {
                continue;
            }
            wSum += w;
            meanX += w * points[i].X;
            meanY += w * points[i].Y;
        }

        if (wSum <= SingularDeterminantEpsilon)
        {
            return false;
        }

        meanX /= wSum;
        meanY /= wSum;

        double distSum = 0.0;
        for (int i = 0; i < n; i++)
        {
            double w = weights[i];
            if (w <= 0.0)
            {
                continue;
            }
            double dx = points[i].X - meanX;
            double dy = points[i].Y - meanY;
            distSum += w * Math.Sqrt((dx * dx) + (dy * dy));
        }

        double meanDist = distSum / wSum;
        if (meanDist <= SingularDeterminantEpsilon)
        {
            return false;
        }

        // [Math Why]: Hartley isotropic scaling s = sqrt(2) / meanDist centers the design matrix condition number near O(1).
        double scale = RootTwo / meanDist;

        // Solve trace-normalized conic A*u^2 + B*u*v + C*v^2 + D*u + E*v + F = 0 with A + C = 1 => C = 1 - A:
        // (A)*(u^2 - v^2) + B*(u*v) + D*u + E*v + F = -v^2
        Span<double> ata = stackalloc double[NormalMatrixSize];
        Span<double> atb = stackalloc double[AlgebraicBasisCount];
        Span<double> basis = stackalloc double[AlgebraicBasisCount];
        Span<double> sol = stackalloc double[AlgebraicBasisCount];
        ata.Clear();
        atb.Clear();

        for (int i = 0; i < n; i++)
        {
            double w = weights[i];
            if (w <= 0.0)
            {
                continue;
            }

            double u = (points[i].X - meanX) * scale;
            double v = (points[i].Y - meanY) * scale;
            double uu = u * u;
            double vv = v * v;

            basis[0] = uu - vv;
            basis[1] = u * v;
            basis[2] = u;
            basis[3] = v;
            basis[4] = 1.0;
            double rhs = -vv;

            for (int r = 0; r < AlgebraicBasisCount; r++)
            {
                double wBr = w * basis[r];
                atb[r] += wBr * rhs;
                int rowOffset = r * AlgebraicBasisCount;
                for (int c = r; c < AlgebraicBasisCount; c++)
                {
                    ata[rowOffset + c] += wBr * basis[c];
                }
            }
        }

        for (int r = 0; r < AlgebraicBasisCount; r++)
        {
            ata[(r * AlgebraicBasisCount) + r] += TikhonovRegularization;
            for (int c = 0; c < r; c++)
            {
                ata[(r * AlgebraicBasisCount) + c] = ata[(c * AlgebraicBasisCount) + r];
            }
        }

        if (!SmallMatrixSolver.TrySolveLinearSystem(ata, atb, AlgebraicBasisCount, sol))
        {
            return false;
        }

        double A = sol[0];
        double B = sol[1];
        double C = 1.0 - A;
        double D = sol[2];
        double E = sol[3];
        double F = sol[4];

        // Verify elliptic discriminant: 4*A*C - B^2 > 0
        double disc = (FourFactor * A * C) - (B * B);
        if (disc <= SingularDeterminantEpsilon)
        {
            return false;
        }

        // Normalized center (u0, v0) where gradient of conic vanishes: [2A  B; B  2C] [u0; v0] = -[D; E]
        double u0 = ((B * E) - (TwoFactor * C * D)) / disc;
        double v0 = ((B * D) - (TwoFactor * A * E)) / disc;

        // Evaluate conic at center: F_c = A*u0^2 + B*u0*v0 + C*v0^2 + D*u0 + E*v0 + F = F + 0.5*(D*u0 + E*v0)
        double fCenter = F + (HalfFactor * ((D * u0) + (E * v0)));
        double negFCenter = -fCenter;
        if (negFCenter <= SingularDeterminantEpsilon)
        {
            return false;
        }

        // Eigenvalues of quadratic form [A, B/2; B/2, C]:
        double diffAC = A - C;
        double eigenRoot = Math.Sqrt((diffAC * diffAC) + (B * B));
        double lambda1 = HalfFactor * (A + C - eigenRoot);
        double lambda2 = HalfFactor * (A + C + eigenRoot);

        if (lambda1 <= SingularDeterminantEpsilon || lambda2 <= SingularDeterminantEpsilon)
        {
            return false;
        }

        double normA = Math.Sqrt(negFCenter / lambda1);
        double normB = Math.Sqrt(negFCenter / lambda2);
        double theta = HalfFactor * Math.Atan2(-B, C - A);

        cx = meanX + (u0 / scale);
        cy = meanY + (v0 / scale);
        semiMajor = normA / scale;
        semiMinor = normB / scale;
        angleRad = theta;

        NormalizeCanonicalAxesAndAngle(ref semiMajor, ref semiMinor, ref angleRad);
        return true;
    }

    /// <summary>
    /// Refines $(C_x, C_y, A, B, \theta)$ by minimizing weighted Sampson geometric distance
    /// $d_i = \frac{f(x_i, y_i)}{2 \|\nabla f(x_i, y_i)\|}$ where $f(x,y) = (u/A)^2 + (v/B)^2 - 1$.
    /// </summary>
    private static void RefineEllipseSampsonGaussNewton(
        ReadOnlySpan<Vector2> points,
        ReadOnlySpan<double> weights,
        ref double cx,
        ref double cy,
        ref double aAxis,
        ref double bAxis,
        ref double angleRad)
    {
        const int ParamCount = 5;
        Span<double> jtj = stackalloc double[ParamCount * ParamCount];
        Span<double> jtr = stackalloc double[ParamCount];
        Span<double> jac = stackalloc double[ParamCount];
        Span<double> step = stackalloc double[ParamCount];

        int n = points.Length;

        for (int iter = 0; iter < GaussNewtonIterations; iter++)
        {
            jtj.Clear();
            jtr.Clear();

            (double sinT, double cosT) = Math.SinCos(angleRad);
            double invA = 1.0 / aAxis;
            double invB = 1.0 / bAxis;
            double invA2 = invA * invA;
            double invB2 = invB * invB;

            for (int i = 0; i < n; i++)
            {
                double w = weights[i];
                if (w <= 0.0)
                {
                    continue;
                }

                double dx = points[i].X - cx;
                double dy = points[i].Y - cy;
                double u = (dx * cosT) + (dy * sinT);
                double v = (-dx * sinT) + (dy * cosT);

                double ua = u * invA;
                double vb = v * invB;
                double f = (ua * ua) + (vb * vb) - 1.0;

                // Spatial gradient of algebraic level set f(x,y)/2: gradHalf = ((u/a^2) e_u + (v/b^2) e_v)
                double gu = u * invA2;
                double gv = v * invB2;
                double gradNorm = Math.Sqrt((gu * gu) + (gv * gv));
                if (gradNorm <= SingularDeterminantEpsilon)
                {
                    continue;
                }

                double invTwoGradNorm = HalfFactor / gradNorm;
                double residual = f * invTwoGradNorm;

                // First-order partial derivatives of residual with respect to (cx, cy, a, b, theta)
                double dfDcx = -TwoFactor * ((gu * cosT) - (gv * sinT));
                double dfDcy = -TwoFactor * ((gu * sinT) + (gv * cosT));
                double dfDa = -TwoFactor * ua * ua * invA;
                double dfDb = -TwoFactor * vb * vb * invB;
                double dfDtheta = TwoFactor * u * v * (invA2 - invB2);

                jac[0] = dfDcx * invTwoGradNorm;
                jac[1] = dfDcy * invTwoGradNorm;
                jac[2] = dfDa * invTwoGradNorm;
                jac[3] = dfDb * invTwoGradNorm;
                jac[4] = dfDtheta * invTwoGradNorm;

                for (int r = 0; r < ParamCount; r++)
                {
                    double wJr = w * jac[r];
                    jtr[r] -= wJr * residual;
                    int rowBase = r * ParamCount;
                    for (int c = r; c < ParamCount; c++)
                    {
                        jtj[rowBase + c] += wJr * jac[c];
                    }
                }
            }

            for (int r = 0; r < ParamCount; r++)
            {
                jtj[(r * ParamCount) + r] += 1e-6;
                for (int c = 0; c < r; c++)
                {
                    jtj[(r * ParamCount) + c] = jtj[(c * ParamCount) + r];
                }
            }

            if (!SmallMatrixSolver.TrySolveLinearSystem(jtj, jtr, ParamCount, step))
            {
                break;
            }

            double nextA = aAxis + step[2];
            double nextB = bAxis + step[3];
            if (nextA <= MinimumRobustScalePixels || nextB <= MinimumRobustScalePixels)
            {
                break;
            }

            cx += step[0];
            cy += step[1];
            aAxis = nextA;
            bAxis = nextB;
            angleRad += step[4];

            double stepNormSq = (step[0] * step[0]) + (step[1] * step[1]) + (step[2] * step[2]) + (step[3] * step[3]) + (step[4] * step[4]);
            if (stepNormSq < GaussNewtonStepConvergenceSq)
            {
                break;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double ComputeSignedSampsonResidual(
        double x,
        double y,
        double cx,
        double cy,
        double aAxis,
        double bAxis,
        double angleRad)
    {
        (double sinT, double cosT) = Math.SinCos(angleRad);
        double dx = x - cx;
        double dy = y - cy;
        double u = (dx * cosT) + (dy * sinT);
        double v = (-dx * sinT) + (dy * cosT);

        double invA = 1.0 / aAxis;
        double invB = 1.0 / bAxis;
        double ua = u * invA;
        double vb = v * invB;
        double f = (ua * ua) + (vb * vb) - 1.0;

        double gu = u * invA * invA;
        double gv = v * invB * invB;
        double gradNorm = Math.Sqrt((gu * gu) + (gv * gv));
        return gradNorm > SingularDeterminantEpsilon ? (HalfFactor * f) / gradNorm : 0.0;
    }

    private static void NormalizeCanonicalAxesAndAngle(ref double aAxis, ref double bAxis, ref double angleRad)
    {
        if (bAxis > aAxis)
        {
            (aAxis, bAxis) = (bAxis, aAxis);
            angleRad += Math.PI * HalfFactor;
        }

        // Canonicalize angle into (-pi/2, pi/2]
        while (angleRad <= -Math.PI * HalfFactor)
        {
            angleRad += Math.PI;
        }
        while (angleRad > Math.PI * HalfFactor)
        {
            angleRad -= Math.PI;
        }
    }

    private static double EstimateRobustSigma(ReadOnlySpan<double> residuals, int count)
    {
        Span<double> sorted = count <= MaxStackAllocPoints ? stackalloc double[count] : new double[count];
        residuals.Slice(0, count).CopyTo(sorted);
        sorted.Sort();
        double median = (count & 1) == 1
            ? sorted[count >> 1]
            : HalfFactor * (sorted[(count >> 1) - 1] + sorted[count >> 1]);
        return Math.Max(MinimumRobustScalePixels, median * MadToSigmaFactor);
    }
}
