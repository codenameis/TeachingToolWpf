using System.Collections.Immutable;

namespace VisionAlgorithm.OpenSrc.PatternMatching;

public sealed record MatchResult(ImmutableArray<ShapeMatch> Matches, ShapeSearchDiagnostics Diagnostics);
