using System.Runtime.CompilerServices;

namespace VisionAlgorithm.OpenSrc.PatternMatching;

/// <summary>
/// Validates topological outer/inner contour chain presence and cross-chain contrast polarity consistency.
/// </summary>
internal readonly struct ChainConsistencyCheck : IVerificationStage
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Evaluate(ref VerificationAccumulators acc, in VerificationThresholds thresholds)
    {
        if (!thresholds.HasChains || acc.OuterCount <= 0 || acc.InnerCount <= 0)
        {
            return true;
        }

        if ((acc.OuterSum / acc.OuterCount) >= VerificationThresholds.OuterChainStrongThreshold &&
            (acc.InnerSum / acc.InnerCount) <= VerificationThresholds.InnerChainMissingThreshold)
        {
            acc.MeanScore *= VerificationThresholds.ChainMismatchPenaltyFactor;
        }
        else
        {
            var outerMean = acc.OuterSigned / acc.OuterCount;
            var innerMean = acc.InnerSigned / acc.InnerCount;
            var weaker = Math.Min(Math.Abs(outerMean), Math.Abs(innerMean));
            if ((outerMean * innerMean < 0 && weaker >= VerificationThresholds.ChainPolarityWeakerSignThreshold) ||
                weaker < VerificationThresholds.ChainPolarityMinimumStrength)
            {
                acc.MeanScore *= VerificationThresholds.ChainMismatchPenaltyFactor;
            }
        }

        return true;
    }
}
