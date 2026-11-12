using VisionAlgorithm.OpenSrc.PatternMatching;
using VisionAlgorithm.OpenSrc.ShapeDetection;

namespace VisionInspection.Cli;

public sealed record RefinementSummary(string Status, double? InitialResidual, double? FinalResidual,
    int Iterations, int Correspondences, bool AngleObservable);
