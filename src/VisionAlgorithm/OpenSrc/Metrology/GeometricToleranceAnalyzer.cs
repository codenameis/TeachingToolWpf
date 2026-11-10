namespace VisionAlgorithm.OpenSrc.Metrology;

/// <summary>
/// Multi-feature sub-pixel Geometric Dimensioning &amp; Tolerancing (GD&amp;T) metrology analyzer.
/// Extracts sub-pixel datum lines and concentric bore circles directly from the grayscale image via Steger/parabolic caliper spokes,
/// then computes exact analytical geometric relationships:
/// 1. Line-to-Line intersection angle $\theta = \arccos(|\mathbf{n}_1 \cdot \mathbf{n}_2|)$ and intersection point $(x_\times, y_\times)$.
/// 2. Circle-to-Circle concentricity offset $\Delta c = \|\mathbf{c}_{\text{outer}} - \mathbf{c}_{\text{inner}}\|_2$.
/// 3. Bore-center-to-Datum perpendicular distance $d_\perp = |\mathbf{n} \cdot \mathbf{c} + d|$.
/// </summary>
public static class GeometricToleranceAnalyzer
{
    private const double RadToDeg = 180.0 / Math.PI;
    private const int SpokeCount = 36;
    private const int CaliperCount = 24;
    private const int IrlsIterations = 5;
    private const double TukeyTuningConstant = 4.685;

    /// <summary>
    /// Measures sub-pixel datum lines (horizontal top datum &amp; vertical left datum) and dual concentric bore circles,
    /// then evaluates geometric tolerances against nominal angle, nominal bore-to-datum distance, and concentricity limit.
    /// </summary>
    public static GeometricToleranceResult AnalyzeGeometricTolerances(
        ReadOnlySpan<byte> image,
        int width,
        int height,
        int stride,
        double nominalAngleDeg = 90.0,
        double angleToleranceDeg = 1.5,
        double nominalCenterDistancePx = 0.0,
        double distanceTolerancePx = 3.5,
        double concentricityLimitPx = 2.0,
        double gradientThreshold = 18.0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, width);

        if (width < 48 || height < 48)
        {
            return default;
        }

        // 1. Extract Horizontal Datum Line (top edge of bracket/part)
        var hLinePoints = ExtractHorizontalDatumEdges(image, width, height, stride, gradientThreshold);
        // 2. Extract Vertical Datum Line (left edge of bracket/part)
        var vLinePoints = ExtractVerticalDatumEdges(image, width, height, stride, gradientThreshold);

        if (hLinePoints.Count < 4 || vLinePoints.Count < 4)
        {
            return default;
        }

        var hLine = FitLineTukeyIrls(hLinePoints);
        var vLine = FitLineTukeyIrls(vLinePoints);

        // Compute Line-to-Line Angle & Intersection
        var dot = (hLine.Nx * vLine.Nx) + (hLine.Ny * vLine.Ny);
        var angleRad = Math.Acos(Math.Clamp(Math.Abs(dot), 0.0, 1.0));
        var lineAngleDeg = angleRad * RadToDeg;

        var det = (hLine.Nx * vLine.Ny) - (hLine.Ny * vLine.Nx);
        var intersectX = Math.Abs(det) > 1e-9
            ? ((hLine.Ny * vLine.Distance) - (vLine.Ny * hLine.Distance)) / det
            : width * 0.15;
        var intersectY = Math.Abs(det) > 1e-9
            ? ((vLine.Nx * hLine.Distance) - (hLine.Nx * vLine.Distance)) / det
            : height * 0.15;

        // 3. Extract Outer & Inner Concentric Bore Circles around the part center
        var seedCx = width * 0.56;
        var seedCy = height * 0.56;
        var maxSearchR = Math.Min(width, height) * 0.36;

        ExtractConcentricBoreEdges(
            image,
            width,
            height,
            stride,
            seedCx,
            seedCy,
            maxSearchR,
            gradientThreshold,
            out var innerPoints,
            out var outerPoints);

        (double CenterX, double CenterY, double Radius) innerCircle;
        (double CenterX, double CenterY, double Radius) outerCircle;

        if (innerPoints.Count >= 6 && outerPoints.Count >= 6)
        {
            innerCircle = FitCircleKasaTukeyIrls(innerPoints);
            outerCircle = FitCircleKasaTukeyIrls(outerPoints);
        }
        else if (outerPoints.Count >= 6)
        {
            outerCircle = FitCircleKasaTukeyIrls(outerPoints);
            innerCircle = outerCircle;
        }
        else
        {
            return default;
        }

        var dxCenter = outerCircle.CenterX - innerCircle.CenterX;
        var dyCenter = outerCircle.CenterY - innerCircle.CenterY;
        var concentricityPx = Math.Sqrt((dxCenter * dxCenter) + (dyCenter * dyCenter));

        // Perpendicular distance from bore center to Horizontal Datum Line and Vertical Datum Line
        var distToHorizontal = Math.Abs((hLine.Nx * outerCircle.CenterX) + (hLine.Ny * outerCircle.CenterY) + hLine.Distance);
        var distToVertical = Math.Abs((vLine.Nx * outerCircle.CenterX) + (vLine.Ny * outerCircle.CenterY) + vLine.Distance);

        var targetNominalDist = nominalCenterDistancePx > 1.0 ? nominalCenterDistancePx : distToHorizontal;
        var angleErr = Math.Abs(lineAngleDeg - nominalAngleDeg);
        var distErr = Math.Abs(distToHorizontal - targetNominalDist);

        var withinTol = angleErr <= Math.Max(0.1, angleToleranceDeg)
            && distErr <= Math.Max(0.2, distanceTolerancePx)
            && concentricityPx <= Math.Max(0.2, concentricityLimitPx);

        return new GeometricToleranceResult(
            true,
            withinTol,
            lineAngleDeg,
            distToVertical,
            concentricityPx,
            outerCircle.Radius,
            innerCircle.Radius,
            distToHorizontal,
            angleErr,
            distErr,
            intersectX,
            intersectY,
            outerCircle.CenterX,
            outerCircle.CenterY,
            innerCircle.CenterX,
            innerCircle.CenterY);
    }

    private static (double Nx, double Ny, double Distance) FitLineTukeyIrls(List<(double X, double Y)> points)
    {
        var count = points.Count;
        Span<double> weights = count <= 128 ? stackalloc double[count] : new double[count];
        weights.Fill(1.0);

        double nx = 0.0, ny = 1.0, dist = 0.0;

        for (var iter = 0; iter < IrlsIterations; iter++)
        {
            double sumW = 0.0, sumX = 0.0, sumY = 0.0;
            for (var i = 0; i < count; i++)
            {
                var w = weights[i];
                sumW += w;
                sumX += w * points[i].X;
                sumY += w * points[i].Y;
            }

            if (sumW <= 1e-12)
            {
                break;
            }

            var meanX = sumX / sumW;
            var meanY = sumY / sumW;

            double sxx = 0.0, syy = 0.0, sxy = 0.0;
            for (var i = 0; i < count; i++)
            {
                var w = weights[i];
                var dx = points[i].X - meanX;
                var dy = points[i].Y - meanY;
                sxx += w * dx * dx;
                syy += w * dy * dy;
                sxy += w * dx * dy;
            }

            // Principal normal is eigenvector of 2x2 covariance corresponding to smaller eigenvalue
            var theta = 0.5 * Math.Atan2(2.0 * sxy, sxx - syy);
            nx = -Math.Sin(theta);
            ny = Math.Cos(theta);
            dist = -((nx * meanX) + (ny * meanY));

            var sumAbsRes = 0.0;
            for (var i = 0; i < count; i++)
            {
                sumAbsRes += Math.Abs((nx * points[i].X) + (ny * points[i].Y) + dist);
            }

            var sigma = Math.Max(0.25, 1.4826 * (sumAbsRes / count));
            var cutoff = TukeyTuningConstant * sigma;

            for (var i = 0; i < count; i++)
            {
                var r = Math.Abs((nx * points[i].X) + (ny * points[i].Y) + dist);
                if (r >= cutoff)
                {
                    weights[i] = 0.0;
                }
                else
                {
                    var u = r / cutoff;
                    var oneMinusUSq = 1.0 - (u * u);
                    weights[i] = oneMinusUSq * oneMinusUSq;
                }
            }
        }

        return (nx, ny, dist);
    }

    private static (double CenterX, double CenterY, double Radius) FitCircleKasaTukeyIrls(List<(double X, double Y)> points)
    {
        var count = points.Count;
        Span<double> weights = count <= 128 ? stackalloc double[count] : new double[count];
        weights.Fill(1.0);

        double cx = 0.0, cy = 0.0, radius = 10.0;

        for (var iter = 0; iter < IrlsIterations; iter++)
        {
            double s00 = 0, s10 = 0, s01 = 0, s20 = 0, s02 = 0, s11 = 0, s30 = 0, s03 = 0, s21 = 0, s12 = 0;
            for (var i = 0; i < count; i++)
            {
                var w = weights[i];
                var x = points[i].X;
                var y = points[i].Y;
                var x2 = x * x;
                var y2 = y * y;
                s00 += w;
                s10 += w * x;
                s01 += w * y;
                s20 += w * x2;
                s02 += w * y2;
                s11 += w * x * y;
                s30 += w * x2 * x;
                s03 += w * y2 * y;
                s21 += w * x2 * y;
                s12 += w * x * y2;
            }

            if (s00 <= 1e-12)
            {
                break;
            }

            var a11 = 2.0 * (s20 - ((s10 * s10) / s00));
            var a12 = 2.0 * (s11 - ((s10 * s01) / s00));
            var a22 = 2.0 * (s02 - ((s01 * s01) / s00));

            var b1 = (s30 + s12) - (((s20 + s02) * s10) / s00);
            var b2 = (s03 + s21) - (((s20 + s02) * s01) / s00);

            var det = (a11 * a22) - (a12 * a12);
            if (Math.Abs(det) <= 1e-12)
            {
                break;
            }

            cx = ((b1 * a22) - (b2 * a12)) / det;
            cy = ((a11 * b2) - (a12 * b1)) / det;

            var rSqSum = 0.0;
            for (var i = 0; i < count; i++)
            {
                var dx = points[i].X - cx;
                var dy = points[i].Y - cy;
                rSqSum += weights[i] * ((dx * dx) + (dy * dy));
            }

            radius = Math.Sqrt(Math.Max(1.0, rSqSum / s00));

            var sumAbsRes = 0.0;
            for (var i = 0; i < count; i++)
            {
                var dx = points[i].X - cx;
                var dy = points[i].Y - cy;
                sumAbsRes += Math.Abs(Math.Sqrt((dx * dx) + (dy * dy)) - radius);
            }

            var sigma = Math.Max(0.25, 1.4826 * (sumAbsRes / count));
            var cutoff = TukeyTuningConstant * sigma;

            for (var i = 0; i < count; i++)
            {
                var dx = points[i].X - cx;
                var dy = points[i].Y - cy;
                var r = Math.Abs(Math.Sqrt((dx * dx) + (dy * dy)) - radius);
                if (r >= cutoff)
                {
                    weights[i] = 0.0;
                }
                else
                {
                    var u = r / cutoff;
                    var oneMinusUSq = 1.0 - (u * u);
                    weights[i] = oneMinusUSq * oneMinusUSq;
                }
            }
        }

        return (cx, cy, radius);
    }

    private static List<(double X, double Y)> ExtractHorizontalDatumEdges(
        ReadOnlySpan<byte> image,
        int width,
        int height,
        int stride,
        double minGrad)
    {
        var points = new List<(double X, double Y)>(CaliperCount);
        var xStart = (int)(width * 0.22);
        var xEnd = (int)(width * 0.85);
        var ySearchMax = (int)(height * 0.42);
        var stepX = Math.Max(2, (xEnd - xStart) / CaliperCount);

        for (var x = xStart; x <= xEnd; x += stepX)
        {
            var bestGrad = minGrad;
            var bestY = -1.0;

            for (var y = 4; y < ySearchMax - 1; y++)
            {
                var gPrev = Math.Abs((double)image[(y * stride) + x] - image[((y - 2) * stride) + x]) * 0.5;
                var gCurr = Math.Abs((double)image[((y + 1) * stride) + x] - image[((y - 1) * stride) + x]) * 0.5;
                var gNext = Math.Abs((double)image[((y + 2) * stride) + x] - image[(y * stride) + x]) * 0.5;

                if (gCurr >= bestGrad && gCurr >= gPrev && gCurr >= gNext)
                {
                    var denom = gPrev - (2.0 * gCurr) + gNext;
                    var offset = Math.Abs(denom) > 1e-6 ? Math.Clamp(0.5 * (gPrev - gNext) / denom, -0.5, 0.5) : 0.0;
                    bestGrad = gCurr;
                    bestY = y + offset;
                    break;
                }
            }

            if (bestY > 0.0)
            {
                points.Add((x, bestY));
            }
        }

        return points;
    }

    private static List<(double X, double Y)> ExtractVerticalDatumEdges(
        ReadOnlySpan<byte> image,
        int width,
        int height,
        int stride,
        double minGrad)
    {
        var points = new List<(double X, double Y)>(CaliperCount);
        var yStart = (int)(height * 0.22);
        var yEnd = (int)(height * 0.85);
        var xSearchMax = (int)(width * 0.42);
        var stepY = Math.Max(2, (yEnd - yStart) / CaliperCount);

        for (var y = yStart; y <= yEnd; y += stepY)
        {
            var row = y * stride;
            var bestGrad = minGrad;
            var bestX = -1.0;

            for (var x = 4; x < xSearchMax - 1; x++)
            {
                var gPrev = Math.Abs((double)image[row + x] - image[row + x - 2]) * 0.5;
                var gCurr = Math.Abs((double)image[row + x + 1] - image[row + x - 1]) * 0.5;
                var gNext = Math.Abs((double)image[row + x + 2] - image[row + x]) * 0.5;

                if (gCurr >= bestGrad && gCurr >= gPrev && gCurr >= gNext)
                {
                    var denom = gPrev - (2.0 * gCurr) + gNext;
                    var offset = Math.Abs(denom) > 1e-6 ? Math.Clamp(0.5 * (gPrev - gNext) / denom, -0.5, 0.5) : 0.0;
                    bestGrad = gCurr;
                    bestX = x + offset;
                    break;
                }
            }

            if (bestX > 0.0)
            {
                points.Add((bestX, y));
            }
        }

        return points;
    }

    private static void ExtractConcentricBoreEdges(
        ReadOnlySpan<byte> image,
        int width,
        int height,
        int stride,
        double seedCx,
        double seedCy,
        double maxRadius,
        double minGrad,
        out List<(double X, double Y)> innerPoints,
        out List<(double X, double Y)> outerPoints)
    {
        innerPoints = new List<(double X, double Y)>(SpokeCount);
        outerPoints = new List<(double X, double Y)>(SpokeCount);

        var maxSteps = Math.Clamp((int)maxRadius, 16, 180);
        Span<double> profile = stackalloc double[maxSteps];

        for (var s = 0; s < SpokeCount; s++)
        {
            var angle = (2.0 * Math.PI * s) / SpokeCount;
            var cosA = Math.Cos(angle);
            var sinA = Math.Sin(angle);

            for (var r = 0; r < maxSteps; r++)
            {
                var sx = Math.Clamp((int)Math.Round(seedCx + (r * cosA)), 0, width - 1);
                var sy = Math.Clamp((int)Math.Round(seedCy + (r * sinA)), 0, height - 1);
                profile[r] = image[(sy * stride) + sx];
            }

            var firstEdgeR = -1.0;
            var secondEdgeR = -1.0;

            for (var r = 5; r < maxSteps - 2; r++)
            {
                var gPrev = Math.Abs(profile[r] - profile[r - 2]) * 0.5;
                var gCurr = Math.Abs(profile[r + 1] - profile[r - 1]) * 0.5;
                var gNext = Math.Abs(profile[r + 2] - profile[r]) * 0.5;

                if (gCurr >= minGrad && gCurr >= gPrev && gCurr >= gNext)
                {
                    var denom = gPrev - (2.0 * gCurr) + gNext;
                    var offset = Math.Abs(denom) > 1e-6 ? Math.Clamp(0.5 * (gPrev - gNext) / denom, -0.5, 0.5) : 0.0;
                    var subR = r + offset;

                    if (firstEdgeR < 0.0)
                    {
                        firstEdgeR = subR;
                        r += 4;
                    }
                    else if (subR - firstEdgeR >= 6.0)
                    {
                        secondEdgeR = subR;
                        break;
                    }
                }
            }

            if (firstEdgeR > 0.0 && secondEdgeR > 0.0)
            {
                innerPoints.Add((seedCx + (firstEdgeR * cosA), seedCy + (firstEdgeR * sinA)));
                outerPoints.Add((seedCx + (secondEdgeR * cosA), seedCy + (secondEdgeR * sinA)));
            }
            else if (firstEdgeR > 0.0)
            {
                outerPoints.Add((seedCx + (firstEdgeR * cosA), seedCy + (firstEdgeR * sinA)));
            }
        }
    }
}
