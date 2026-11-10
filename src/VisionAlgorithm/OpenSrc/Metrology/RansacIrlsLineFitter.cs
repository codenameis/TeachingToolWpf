// File: src/VisionAlgorithm/OpenSrc/Metrology/RansacIrlsLineFitter.cs
using System.Numerics;
using System.Runtime.CompilerServices;
using VisionAlgorithm.OpenSrc.Common;

namespace VisionAlgorithm.OpenSrc.Metrology;

/// <summary>
/// Represents a 2D line in normal Hesse form $n_x x + n_y y + d = 0$ with $\|n\|_2 = 1$ implementing <see cref="IVisionToolResult"/>.
/// </summary>
public readonly record struct FittedLine2D(
    bool IsValid,
    float NormalX,
    float NormalY,
    float DistanceFromOrigin,
    Vector2 Centroid,
    Vector2 Direction,
    int InlierCount,
    float RmsResidualPixels) : IVisionToolResult
{
    private const float RadiansToDegrees = 180.0f / MathF.PI;
    private const float DefaultHalfLineSpan = 30.0f;

    public Vector2 Normal => new(NormalX, NormalY);

    public VisionResultHeader Header => IsValid
        ? VisionResultHeader.SucceededMeasurement(
            category: VisionToolCategory.SubpixelMetrology,
            qualityScore: Math.Clamp(1.0f - (RmsResidualPixels * 0.5f), 0.0f, 1.0f),
            primaryMetric: RmsResidualPixels)
        : VisionResultHeader.Degenerate(VisionToolCategory.SubpixelMetrology);

    public FixturePose2D WorldPose => IsValid
        ? new FixturePose2D(Centroid.X, Centroid.Y, MathF.Atan2(Direction.Y, Direction.X), 1.0f)
        : FixturePose2D.Identity;

    public Vector2 ReferencePointWorld => Centroid;

    /// <summary>Evaluates the signed orthogonal distance from <paramref name="point"/> to the line.</summary>
    public float SignedDistance(Vector2 point)
        => (NormalX * point.X) + (NormalY * point.Y) + DistanceFromOrigin;

    public VisionSpcRecord ToSpcRecord(
        int nodeId = 0,
        Guid roiId = default,
        double specLsl = double.NaN,
        double specUsl = double.NaN)
    {
        float angleDeg = MathF.Atan2(Direction.Y, Direction.X) * RadiansToDegrees;
        float marginRatio = VisionSpcRecord.ComputeSpecMarginRatio(angleDeg, specLsl, specUsl);

        return new VisionSpcRecord(
            NodeId: nodeId,
            RoiId: roiId,
            Header: Header,
            TotalModelCount: 1,
            RunnerUpModelIndex: VisionResultHeader.UnmatchedModelIndex,
            RunnerUpScore: 0.0f,
            WorldX: Centroid.X,
            WorldY: Centroid.Y,
            WorldAngleDeg: angleDeg,
            Scale: 1.0f,
            DeltaX: 0.0f,
            DeltaY: 0.0f,
            DeltaAngleDeg: 0.0f,
            PrimaryMetric: angleDeg,
            SpecLsl: specLsl,
            SpecUsl: specUsl,
            SpecMarginRatio: marginRatio,
            MeanGrayValue: 0.0f,
            ContrastOrStdDev: 0.0f,
            RmsFitErrorPixels: RmsResidualPixels,
            AuxMetric1: DistanceFromOrigin,
            AuxMetric2: NormalX,
            AuxMetric3: NormalY,
            HitCount: InlierCount,
            TotalSampleCount: InlierCount,
            DecodedText: string.Empty);
    }

    public VisionReplayPayload ToReplayPayload()
    {
        if (!IsValid)
        {
            return VisionReplayPayload.Empty;
        }

        Vector2 p1 = Centroid - (Direction * DefaultHalfLineSpan);
        Vector2 p2 = Centroid + (Direction * DefaultHalfLineSpan);
        string label = $"FittedLine: Angle={MathF.Atan2(Direction.Y, Direction.X) * RadiansToDegrees:F2}° Inliers={InlierCount} RMS={RmsResidualPixels:F3}px";

        return new VisionReplayPayload(
            ShapeKind: ReplayShapeKind.LineSegment,
            WorldPose: WorldPose,
            WidthOrRadius: DefaultHalfLineSpan * 2.0f,
            HeightOrSpan: RmsResidualPixels,
            SegmentAStart: p1,
            SegmentAEnd: p2,
            SegmentBStart: Vector2.Zero,
            SegmentBEnd: Vector2.Zero,
            SubRegions: ReadOnlyMemory<ReplaySubRegion>.Empty,
            InlierPointsWorld: ReadOnlyMemory<Vector2>.Empty,
            OutlierPointsWorld: ReadOnlyMemory<Vector2>.Empty,
            SummaryLabel: label);
    }
}

/// <summary>
/// Fits a high-precision 2D line to multi-caliper (rake) edge points using deterministic RANSAC hypothesis generation
/// followed by Iteratively Reweighted Least Squares (IRLS) with Tukey's Biweight M-estimator.
/// </summary>
public static class RansacIrlsLineFitter
{
    // [Math Why]: Minimum robust scale floor (0.05 px) prevents Tukey biweight cutoff (4.6851 * sigma >= 0.234 px)
    // from collapsing and rejecting subpixel-accurate caliper inliers (0.01..0.20 px) when >50% of points are collinear.
    private const float MinimumScaleFloor = 0.05f;

    // [Math Why]: Minimum distance between two RANSAC sample points avoids degenerate line hypotheses.
    private const float MinimumSamplePairSeparationSq = 1e-2f;

    // [Perf Why]: 5 IRLS iterations are sufficient for M-estimator convergence after RANSAC initialization.
    private const int DefaultIrlsIterations = 5;

    // [Perf Why]: Maximum deterministic RANSAC pair evaluations caps worst-case runtime while exhausting small-N combinations.
    private const int MaxRansacIterations = 180;

    // [Math Why]: Positive weight sum floor prevents division by zero when all points receive zero Tukey weight.
    private const double MinTotalWeightEpsilon = 1e-9;

    /// <summary>
    /// Fits a robust 2D line to <paramref name="points"/> using RANSAC inlier consensus and Tukey Biweight IRLS refinement.
    /// </summary>
    public static FittedLine2D FitLine(
        ReadOnlySpan<Vector2> points,
        float inlierThresholdPixels,
        InspectionWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (points.Length < 2 || !float.IsFinite(inlierThresholdPixels) || inlierThresholdPixels <= 0f)
        {
            return default;
        }

        int n = points.Length;
        float bestScore = -1f;
        int bestInliers = 0;
        float bestNx = 0f;
        float bestNy = 1f;
        float bestD = 0f;

        // Deterministic stride over all point pairs up to MaxRansacIterations
        int totalPairs = (n * (n - 1)) / 2;
        int step = Math.Max(1, totalPairs / MaxRansacIterations);
        int pairIdx = 0;

        for (int i = 0; i < n - 1; i++)
        {
            Vector2 p0 = points[i];
            for (int j = i + 1; j < n; j++)
            {
                if ((pairIdx++ % step) != 0)
                {
                    continue;
                }

                Vector2 p1 = points[j];
                Vector2 diff = p1 - p0;
                float lenSq = diff.LengthSquared();
                if (lenSq < MinimumSamplePairSeparationSq)
                {
                    continue;
                }

                float invLen = 1f / MathF.Sqrt(lenSq);
                float nx = -diff.Y * invLen;
                float ny = diff.X * invLen;
                float d = -((nx * p0.X) + (ny * p0.Y));

                // MSAC (M-estimator Sample Consensus) truncated quadratic score
                float score = 0f;
                int inliers = 0;
                float threshSq = inlierThresholdPixels * inlierThresholdPixels;

                for (int k = 0; k < n; k++)
                {
                    float r = (nx * points[k].X) + (ny * points[k].Y) + d;
                    float rSq = r * r;
                    if (rSq <= threshSq)
                    {
                        inliers++;
                        score += threshSq - rSq;
                    }
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    bestInliers = inliers;
                    bestNx = nx;
                    bestNy = ny;
                    bestD = d;
                }
            }
        }

        if (bestInliers < 2)
        {
            return default;
        }

        Span<float> weights = workspace.GetFloatBufferA(n);
        Span<float> absResiduals = workspace.GetFloatBufferB(n);

        // Initialize weights from RANSAC inlier set
        for (int i = 0; i < n; i++)
        {
            float r = MathF.Abs((bestNx * points[i].X) + (bestNy * points[i].Y) + bestD);
            weights[i] = r <= inlierThresholdPixels ? 1f : 0f;
        }

        Vector2 centroid = Vector2.Zero;
        Vector2 direction = new(1f, 0f);

        for (int iter = 0; iter < DefaultIrlsIterations; iter++)
        {
            if (!TrySolveWeightedTotalLeastSquares(points, weights, out centroid, out direction, out bestNx, out bestNy, out bestD))
            {
                break;
            }

            // Compute residuals and robust MAD scale estimate over active points
            int residualCount = 0;
            for (int i = 0; i < n; i++)
            {
                float r = MathF.Abs((bestNx * points[i].X) + (bestNy * points[i].Y) + bestD);
                if (r <= inlierThresholdPixels * 2f)
                {
                    absResiduals[residualCount++] = r;
                }
            }

            if (residualCount < 2)
            {
                break;
            }

            float medianSigma = RobustMestimator.EstimateMadSigmaInPlace(absResiduals.Slice(0, residualCount), MinimumScaleFloor);
            float robustSigma = Math.Min(inlierThresholdPixels, medianSigma);
            float cutoff = RobustMestimator.TukeyBiweightConstant * robustSigma;

            for (int i = 0; i < n; i++)
            {
                float r = MathF.Abs((bestNx * points[i].X) + (bestNy * points[i].Y) + bestD);
                weights[i] = RobustMestimator.ComputeTukeyBiweight(r, cutoff);
            }
        }

        // Final weighted total least squares pass and RMS residual calculation
        TrySolveWeightedTotalLeastSquares(points, weights, out centroid, out direction, out bestNx, out bestNy, out bestD);

        double sumWeightedSq = 0.0;
        double sumWeights = 0.0;
        int finalInliers = 0;
        for (int i = 0; i < n; i++)
        {
            float r = (bestNx * points[i].X) + (bestNy * points[i].Y) + bestD;
            if (MathF.Abs(r) <= inlierThresholdPixels)
            {
                finalInliers++;
            }

            float w = weights[i];
            if (w > 0f)
            {
                sumWeightedSq += w * r * r;
                sumWeights += w;
            }
        }

        float rms = sumWeights > 0.0 ? (float)Math.Sqrt(sumWeightedSq / sumWeights) : 0f;
        return new FittedLine2D(
            IsValid: true,
            NormalX: bestNx,
            NormalY: bestNy,
            DistanceFromOrigin: bestD,
            Centroid: centroid,
            Direction: direction,
            InlierCount: finalInliers,
            RmsResidualPixels: rms);
    }

    private static bool TrySolveWeightedTotalLeastSquares(
        ReadOnlySpan<Vector2> points,
        ReadOnlySpan<float> weights,
        out Vector2 centroid,
        out Vector2 direction,
        out float nx,
        out float ny,
        out float d)
    {
        double sumW = 0.0;
        double sumX = 0.0;
        double sumY = 0.0;

        for (int i = 0; i < points.Length; i++)
        {
            double w = weights[i];
            if (w <= 0.0)
            {
                continue;
            }

            sumW += w;
            sumX += w * points[i].X;
            sumY += w * points[i].Y;
        }

        if (sumW <= MinTotalWeightEpsilon)
        {
            centroid = Vector2.Zero;
            direction = Vector2.UnitX;
            nx = 0f;
            ny = 1f;
            d = 0f;
            return false;
        }

        double cx = sumX / sumW;
        double cy = sumY / sumW;
        double muXX = 0.0;
        double muYY = 0.0;
        double muXY = 0.0;

        for (int i = 0; i < points.Length; i++)
        {
            double w = weights[i];
            if (w <= 0.0)
            {
                continue;
            }

            double dx = points[i].X - cx;
            double dy = points[i].Y - cy;
            muXX += w * dx * dx;
            muYY += w * dy * dy;
            muXY += w * dx * dy;
        }

        // [Math Why]: Principal axis of 2x2 weighted scatter matrix has tangent angle theta = 0.5 * atan2(2 * muXY, muXX - muYY).
        double theta = 0.5 * Math.Atan2(2.0 * muXY, muXX - muYY);
        float dirX = (float)Math.Cos(theta);
        float dirY = (float)Math.Sin(theta);

        centroid = new Vector2((float)cx, (float)cy);
        direction = new Vector2(dirX, dirY);
        nx = -dirY;
        ny = dirX;
        d = -((nx * centroid.X) + (ny * centroid.Y));
        return true;
    }
}

/// <summary>
/// Backward-compatible type forwarding alias for <see cref="RansacIrlsLineFitter"/>.
/// </summary>
public static class RansacImsLineFitter
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static FittedLine2D FitLine(
        ReadOnlySpan<Vector2> points,
        float inlierThresholdPixels,
        InspectionWorkspace workspace)
        => RansacIrlsLineFitter.FitLine(points, inlierThresholdPixels, workspace);
}
