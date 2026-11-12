using System.Collections.Immutable;

namespace VisionAlgorithm.OpenSrc.PatternMatching;

public sealed record RefinementDiagnostics(RefinementStatus Status, MatchPose InitialPose, double InitialResidual,
    double FinalResidual, int Iterations, int Correspondences, bool AngleObservable);
