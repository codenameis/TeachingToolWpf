using System.Runtime.CompilerServices;

namespace VisionAlgorithm.OpenSrc.PatternMatching;

/// <summary>
/// Compensates score and re-evaluates inlier contour chains when occluded spatial partitions form a single contiguous region
/// and remaining inlier features retain 2D normal diversity.
/// </summary>
internal readonly struct ContiguousOcclusionCompensation : IVerificationStage
{
    private const double PixelCenterOffset = 0.5;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Evaluate(ref VerificationAccumulators acc, in VerificationThresholds thresholds)
    {
        if (!thresholds.AllowOcclusion ||
            acc.AsymKeyMissing ||
            !thresholds.HasPartitions ||
            thresholds.HasUniformGrid ||
            thresholds.HasElasticPatches ||
            !acc.Model.Options.BuildClutterModel ||
            !acc.Model.Options.UseFineDetailValidation)
        {
            return true;
        }

        var maxOccludedBlocks = Math.Clamp(
            (int)Math.Floor(thresholds.MaxOcclusionRatio * VerificationThresholds.SpatialPartitionCount),
            1,
            VerificationThresholds.MaxOccludedBlockLimit);
        var minInlierRatio = Math.Clamp(1.0 - thresholds.MaxOcclusionRatio, VerificationThresholds.MinInlierRatioFloor, 1.0);

        byte occludedMask = 0;
        var occludedBlocks = 0;
        var inlierBlocks = 0;
        var inlierSum = 0d;
        var inlierCount = 0;
        var inlierSupported = 0;
        for (var p = 0; p < VerificationThresholds.SpatialPartitionCount; p++)
        {
            if (acc.PartitionCount[p] == 0)
            {
                continue;
            }

            var pMean = (thresholds.AllowElastic ? acc.PartitionElasticSum[p] : acc.PartitionRigidSum[p]) / acc.PartitionCount[p];
            var pSupportRatio = acc.PartitionSupported[p] / (double)acc.PartitionCount[p];
            if (pMean < VerificationThresholds.OccludedBlockStrictMeanThreshold ||
                (pMean < VerificationThresholds.OccludedBlockSoftMeanThreshold && pSupportRatio < VerificationThresholds.OccludedBlockSoftSupportThreshold))
            {
                occludedMask |= (byte)(1 << p);
                occludedBlocks++;
            }
            else
            {
                inlierBlocks++;
                inlierSum += thresholds.AllowElastic ? acc.PartitionElasticSum[p] : acc.PartitionRigidSum[p];
                inlierCount += acc.PartitionCount[p];
                inlierSupported += acc.PartitionSupported[p];
            }
        }

        var requiredInlierMean = thresholds.AllowElastic
            ? VerificationThresholds.ElasticInlierRequiredMean
            : VerificationThresholds.RigidInlierRequiredMean;

        if (occludedBlocks >= 1 &&
            occludedBlocks <= maxOccludedBlocks &&
            inlierBlocks >= VerificationThresholds.SpatialPartitionCount - maxOccludedBlocks &&
            inlierCount >= acc.Count * minInlierRatio &&
            (inlierSum / inlierCount) >= requiredInlierMean &&
            ContinuousVerifier.IsContiguousPartitionMask(occludedMask, acc.Model.PartitionAdjacency) &&
            ContinuousVerifier.HasOrthogonalNormalDiversity(
                acc.Image,
                acc.Features.AsSpan(),
                acc.Partitions.AsSpan(),
                occludedMask,
                acc.Pose,
                acc.Model.Options.Polarity))
        {
            var occludedCount = acc.Count - inlierCount;
            var occMean = inlierSum / (inlierCount + (VerificationThresholds.OccludedCountWeight * occludedCount));
            if (occMean > acc.MeanScore)
            {
                acc.MeanScore = occMean;
                acc.Supported = Math.Max(acc.Supported, (int)Math.Round(inlierSupported * (acc.Count / (double)inlierCount)));
            }

            if (thresholds.HasChains)
            {
                RecomputeInlierChainTotals(ref acc, occludedMask);
            }
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void RecomputeInlierChainTotals(ref VerificationAccumulators acc, byte occludedMask)
    {
        acc.OuterSum = 0;
        acc.InnerSum = 0;
        acc.OuterCount = 0;
        acc.InnerCount = 0;
        acc.OuterSigned = 0;
        acc.InnerSigned = 0;
        var polarity = acc.Model.Options.Polarity;
        for (var i = 0; i < acc.Count; i++)
        {
            if ((occludedMask & (1 << acc.Partitions[i])) != 0)
            {
                continue;
            }

            var f = acc.Features[i];
            var fx = acc.Pose.X + (acc.Pose.Scale * ((acc.Cos * f.X) - (acc.Sin * f.Y))) - PixelCenterOffset;
            var fy = acc.Pose.Y + (acc.Pose.Scale * ((acc.Sin * f.X) + (acc.Cos * f.Y))) - PixelCenterOffset;
            var fnx = (acc.Cos * f.NormalX) - (acc.Sin * f.NormalY);
            var fny = (acc.Sin * f.NormalX) + (acc.Cos * f.NormalY);
            acc.Image.SampleDirection(fx, fy, out var fgx, out var fgy);
            var fdot = (fnx * fgx) + (fny * fgy);
            var fsc = polarity == ContrastPolarity.IgnoreLocal ? Math.Abs(fdot) : Math.Max(0, fdot);
            if (acc.Chains[i] == 0)
            {
                acc.OuterSigned += fdot;
                acc.OuterSum += fsc;
                acc.OuterCount++;
            }
            else
            {
                acc.InnerSigned += fdot;
                acc.InnerSum += fsc;
                acc.InnerCount++;
            }
        }
    }
}
