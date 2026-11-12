using System.Collections.Immutable;

namespace VisionAlgorithm.OpenSrc.PatternMatching;

public sealed record CorrelationSuppressionResult(ImmutableArray<CorrelationMatch> Matches, bool MatchLimitExceeded);
