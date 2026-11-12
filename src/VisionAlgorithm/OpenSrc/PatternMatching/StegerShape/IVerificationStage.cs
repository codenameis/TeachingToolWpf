namespace VisionAlgorithm.OpenSrc.PatternMatching;

/// <summary>
/// Zero-allocation static-polymorphic verification stage contract for <see cref="ContinuousVerifier"/>.
/// Returns <see langword="false"/> when branch-and-bound pruning rejects the candidate pose early.
/// </summary>
internal interface IVerificationStage
{
    bool Evaluate(ref VerificationAccumulators accumulators, in VerificationThresholds thresholds);
}
