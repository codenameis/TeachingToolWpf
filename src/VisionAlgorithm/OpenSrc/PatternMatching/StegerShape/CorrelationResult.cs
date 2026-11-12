using System.Collections.Immutable;

namespace VisionAlgorithm.OpenSrc.PatternMatching;

public sealed record CorrelationResult(ImmutableArray<CorrelationMatch> Matches, bool Truncated, double Milliseconds,
    bool MatchLimitExceeded = false);
