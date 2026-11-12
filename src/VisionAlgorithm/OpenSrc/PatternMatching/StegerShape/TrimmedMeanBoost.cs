using System.Runtime.CompilerServices;

namespace VisionAlgorithm.OpenSrc.PatternMatching;

/// <summary>
/// Applies top-80% trimmed-mean score blending for locally elastic candidates whose asymmetry keys are intact.
/// </summary>
internal readonly struct TrimmedMeanBoost : IVerificationStage
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Evaluate(ref VerificationAccumulators acc, in VerificationThresholds thresholds)
    {
        if (acc.AsymKeyMissing ||
            (thresholds.HasOccupiedContext && thresholds.StrictContext && thresholds.MaxOcclusionRatio <= VerificationThresholds.StrictMaxOcclusionGate) ||
            acc.FeatureScores.IsEmpty ||
            acc.Count < VerificationThresholds.TrimmedMeanMinFeatureCount)
        {
            return true;
        }

        acc.FeatureScores.Sort();
        var dropCount = acc.Count / VerificationThresholds.TrimmedMeanDropDivisor;
        var keptSum = 0d;
        for (var i = dropCount; i < acc.Count; i++)
        {
            keptSum += acc.FeatureScores[i];
        }

        var keptMean = keptSum / (acc.Count - dropCount);
        if (keptMean >= VerificationThresholds.TrimmedMeanActivationThreshold)
        {
            acc.MeanScore = (VerificationThresholds.TrimmedMeanKeptWeight * keptMean) +
                            (VerificationThresholds.TrimmedMeanOriginalWeight * acc.MeanScore);
        }

        return true;
    }
}
