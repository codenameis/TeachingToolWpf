using System.Collections.Immutable;

namespace VisionAlgorithm.OpenSrc.PatternMatching;

public enum RefinementStatus
{
    Disabled,
    Converged,
    IterationLimit,
    InsufficientSupport,
    IllConditioned,
    Rejected
}
