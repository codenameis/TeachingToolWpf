using System.Runtime.CompilerServices;

namespace VisionAlgorithm.OpenSrc.PatternMatching;

/// <summary>
/// Evaluates remaining scalar features (and all features when local/uniform-grid elasticity is active),
/// enforcing uniform-grid block coherence fallback and initializing <see cref="VerificationAccumulators.MeanScore"/>.
/// </summary>
internal readonly struct ElasticFeatureAccumulator : IVerificationStage
{
    private const double PixelCenterOffset = 0.5;
    private const int EarlyExitStrideMask = 3;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Evaluate(ref VerificationAccumulators acc, in VerificationThresholds thresholds)
    {
        var polarity = acc.Model.Options.Polarity;
        for (; acc.Index < acc.Count; acc.Index++)
        {
            var feature = acc.Features[acc.Index];
            var x = acc.Pose.X + (acc.Pose.Scale * ((acc.Cos * feature.X) - (acc.Sin * feature.Y))) - PixelCenterOffset;
            var y = acc.Pose.Y + (acc.Pose.Scale * ((acc.Sin * feature.X) + (acc.Cos * feature.Y))) - PixelCenterOffset;
            var nx = (acc.Cos * feature.NormalX) - (acc.Sin * feature.NormalY);
            var ny = (acc.Sin * feature.NormalX) + (acc.Cos * feature.NormalY);
            acc.Image.SampleDirection(x, y, out var gx, out var gy);

            var dot = (nx * gx) + (ny * gy);
            var score = polarity == ContrastPolarity.IgnoreLocal ? Math.Abs(dot) : Math.Max(0, dot);
            if (thresholds.HasChains && polarity == ContrastPolarity.Preserve && dot < 0)
            {
                score = Math.Max(VerificationThresholds.PolarityReverseFloor, VerificationThresholds.PolarityReverseScale * dot);
            }

            var rigidScore = score;
            acc.RigidSum += rigidScore;
            if (rigidScore >= VerificationThresholds.FeatureSupportThreshold)
            {
                acc.RigidSupported++;
                acc.RigidQuadrants |= 1 << ((feature.X >= 0 ? 1 : 0) + (feature.Y >= 0 ? 2 : 0));
            }

            var bestNormalStep = 0d;
            if (thresholds.ElasticFeatures && score < VerificationThresholds.ElasticBypassScoreThreshold)
            {
                score = EvaluateLocalElasticShift(
                    acc.Image,
                    polarity,
                    thresholds.HasUniformGrid,
                    thresholds.ElasticSpan,
                    x,
                    y,
                    nx,
                    ny,
                    score,
                    out bestNormalStep);
            }

            if (thresholds.HasPartitions)
            {
                var p = acc.Partitions[acc.Index];
                acc.PartitionRigidSum[p] += rigidScore;
                acc.PartitionElasticSum[p] += score;
                acc.PartitionCount[p]++;
                if (score >= VerificationThresholds.PartitionSupportThreshold)
                {
                    acc.PartitionSupported[p]++;
                }

                if (thresholds.HasUniformGrid && score >= VerificationThresholds.UniformShiftRecordThreshold)
                {
                    acc.PartitionNormalShiftSum[p] += bestNormalStep;
                    acc.PartitionNormalShiftSq[p] += bestNormalStep * bestNormalStep;
                    acc.PartitionNormalShifted[p]++;
                }
            }

            acc.AccumulateChainsAndAsymmetry(thresholds.HasChains, acc.Index, dot, score, rigidScore, thresholds.HasAsymFlags);
            if (score < VerificationThresholds.UnmatchedFeatureThreshold)
            {
                acc.UnmatchedTotalCount++;
                if (thresholds.HasAsymFlags && acc.AsymFlags[acc.Index] != 0)
                {
                    acc.UnmatchedAsymCount++;
                }
            }

            if (thresholds.HasAsymFlags && acc.AsymFlags[acc.Index] != 0)
            {
                var aq = (feature.X >= acc.SymCenter.X ? 1 : 0) + (feature.Y >= acc.SymCenter.Y ? 2 : 0);
                var signedRigid = polarity == ContrastPolarity.IgnoreLocal ? Math.Abs(dot) : Math.Max(0, dot);
                acc.AsymQuadSum[aq] += signedRigid;
                acc.AsymQuadCount[aq]++;
                if ((acc.AsymFlags[acc.Index] & 1) != 0)
                {
                    acc.AsymQuadXSum[aq] += signedRigid;
                    acc.AsymQuadXCount[aq]++;
                }

                if ((acc.AsymFlags[acc.Index] & 2) != 0)
                {
                    acc.AsymQuadYSum[aq] += signedRigid;
                    acc.AsymQuadYCount[aq]++;
                }
            }

            if (!acc.FeatureScores.IsEmpty)
            {
                acc.FeatureScores[acc.Index] = score;
            }

            acc.Sum += score;
            if (score >= VerificationThresholds.FeatureSupportThreshold)
            {
                acc.Supported++;
                acc.Quadrants |= 1 << ((feature.X >= 0 ? 1 : 0) + (feature.Y >= 0 ? 2 : 0));
            }

            if ((acc.Index & EarlyExitStrideMask) == EarlyExitStrideMask &&
                acc.Index + 1 < acc.Count &&
                double.IsFinite(thresholds.MinimumScore))
            {
                var effectiveMin = (thresholds.ElasticFeatures ||
                                    (thresholds.AllowOcclusion &&
                                     thresholds.HasPartitions &&
                                     !thresholds.HasUniformGrid &&
                                     !thresholds.HasElasticPatches &&
                                     acc.Model.Options.UseFineDetailValidation))
                    ? thresholds.MinimumScore * VerificationThresholds.OcclusionEarlyExitFactor
                    : thresholds.MinimumScore;
                var safeUpperMean = (acc.Sum + ((acc.Count - acc.Index - 1) * thresholds.FeatureUpperBound) +
                    (acc.Count * VerificationThresholds.EarlyExitFloatingPointSlack)) / acc.Count;
                if (thresholds.Greediness <= 0)
                {
                    if (safeUpperMean <= effectiveMin)
                    {
                        return false;
                    }
                }
                else
                {
                    var currentMean = acc.Sum / (acc.Index + 1);
                    if (((1.0 - thresholds.Greediness) * safeUpperMean) + (thresholds.Greediness * currentMean) <= effectiveMin)
                    {
                        return false;
                    }
                }
            }
        }

        ApplyUniformGridCoherenceFallback(ref acc, in thresholds);
        acc.MeanScore = acc.Sum / acc.Count;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double EvaluateLocalElasticShift(
        GradientImage image,
        ContrastPolarity polarity,
        bool hasUniformGrid,
        int elasticSpan,
        double x,
        double y,
        double nx,
        double ny,
        double initialScore,
        out double bestNormalStep)
    {
        // [Perf Why]: Keep tight 1D normal / 2D neighborhood shift search allocation-free on stack registers.
        var score = initialScore;
        bestNormalStep = 0d;
        if (hasUniformGrid)
        {
            for (var step = -elasticSpan; step <= elasticSpan; step++)
            {
                if (step == 0)
                {
                    continue;
                }

                var t = step * VerificationThresholds.ElasticStepPixels;
                image.SampleDirection(x + (t * nx), y + (t * ny), out var sx, out var sy);
                if ((sx * sx) + (sy * sy) < VerificationThresholds.UniformGridMinDirectionSquared)
                {
                    continue;
                }

                var shiftedDot = (nx * sx) + (ny * sy);
                var shifted = (polarity == ContrastPolarity.IgnoreLocal ? Math.Abs(shiftedDot) : Math.Max(0, shiftedDot)) *
                    (1.0 - (VerificationThresholds.ElasticDistanceDecay * (t * t)));
                if (shifted > score)
                {
                    score = shifted;
                    bestNormalStep = t;
                }
            }
        }
        else
        {
            for (var iy = -elasticSpan; iy <= elasticSpan; iy++)
            {
                var dy = iy * VerificationThresholds.ElasticStepPixels;
                for (var ix = -elasticSpan; ix <= elasticSpan; ix++)
                {
                    if (ix == 0 && iy == 0)
                    {
                        continue;
                    }

                    var dx = ix * VerificationThresholds.ElasticStepPixels;
                    image.SampleDirection(x + dx, y + dy, out var sx, out var sy);
                    var shiftedDot = (nx * sx) + (ny * sy);
                    var shifted = (polarity == ContrastPolarity.IgnoreLocal ? Math.Abs(shiftedDot) : Math.Max(0, shiftedDot)) *
                        (1.0 - (VerificationThresholds.ElasticDistanceDecay * Math.Sqrt((dx * dx) + (dy * dy))));
                    if (shifted > score)
                    {
                        score = shifted;
                    }
                }
            }
        }

        return score;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ApplyUniformGridCoherenceFallback(ref VerificationAccumulators acc, in VerificationThresholds thresholds)
    {
        if (!thresholds.HasUniformGrid || !thresholds.HasPartitions || !thresholds.ElasticFeatures)
        {
            return;
        }

        var activeBlocks = 0;
        var supportedBlocks = 0;
        var coherentBlocks = 0;
        for (var p = 0; p < VerificationThresholds.SpatialPartitionCount; p++)
        {
            if (acc.PartitionCount[p] == 0)
            {
                continue;
            }

            activeBlocks++;
            if (acc.PartitionSupported[p] >= Math.Max(1, (int)Math.Ceiling(acc.PartitionCount[p] * VerificationThresholds.UniformBlockSupportFraction)))
            {
                supportedBlocks++;
            }

            var nShift = acc.PartitionNormalShifted[p];
            if (nShift > 0)
            {
                var meanT = acc.PartitionNormalShiftSum[p] / nShift;
                var varT = (acc.PartitionNormalShiftSq[p] / nShift) - (meanT * meanT);
                if (varT <= VerificationThresholds.UniformBlockMaxShiftVariance)
                {
                    coherentBlocks++;
                }
            }
        }

        if (activeBlocks > 0 &&
            (supportedBlocks < Math.Ceiling(activeBlocks * VerificationThresholds.UniformMinSupportedBlocksFraction) ||
             coherentBlocks < Math.Ceiling(activeBlocks * VerificationThresholds.UniformMinCoherentBlocksFraction)))
        {
            acc.Sum = acc.RigidSum;
            acc.Supported = acc.RigidSupported;
            acc.Quadrants = acc.RigidQuadrants;
        }
    }
}
