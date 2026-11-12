using System.Runtime.CompilerServices;

namespace VisionAlgorithm.OpenSrc.PatternMatching;

/// <summary>
/// Penalizes non-ring shape candidates in strict context mode when one or more populated spatial partitions completely vanish.
/// </summary>
internal readonly struct PartitionDeadBlockPenalty : IVerificationStage
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Evaluate(ref VerificationAccumulators acc, in VerificationThresholds thresholds)
    {
        if (!thresholds.HasPartitions ||
            !thresholds.StrictContext ||
            thresholds.MaxOcclusionRatio > VerificationThresholds.StrictMaxOcclusionGate ||
            thresholds.HasChains ||
            thresholds.HasUniformGrid ||
            thresholds.HasElasticPatches ||
            !acc.Model.Options.BuildClutterModel ||
            !acc.Model.Options.UseFineDetailValidation)
        {
            return true;
        }

        var deadBlocks = 0;
        var activeBlocks = 0;
        var minBlockMean = 1.0;
        for (var p = 0; p < VerificationThresholds.SpatialPartitionCount; p++)
        {
            if (acc.PartitionCount[p] < VerificationThresholds.MinDeadBlockFeatureCount)
            {
                continue;
            }

            activeBlocks++;
            var pMean = (thresholds.AllowElastic ? acc.PartitionElasticSum[p] : acc.PartitionRigidSum[p]) / acc.PartitionCount[p];
            if (pMean < minBlockMean)
            {
                minBlockMean = pMean;
            }

            if (pMean <= VerificationThresholds.DeadBlockMaxMeanScore && acc.PartitionSupported[p] == 0)
            {
                deadBlocks++;
            }
        }

        if (activeBlocks >= VerificationThresholds.MinActiveBlocksForDeadGate && deadBlocks >= 1)
        {
            acc.AsymKeyMissing = true;
            var blockPenalty = Math.Clamp(
                VerificationThresholds.DeadBlockBasePenalty -
                (VerificationThresholds.DeadBlockPerExtraPenalty * (deadBlocks - 1)) -
                (VerificationThresholds.DeadBlockDeficitSlope * (VerificationThresholds.DeadBlockMaxMeanScore - minBlockMean)),
                VerificationThresholds.DeadBlockMinPenalty,
                VerificationThresholds.DeadBlockBasePenalty);
            acc.MeanScore *= blockPenalty;
        }

        return true;
    }
}
