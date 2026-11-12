using System.Collections.Immutable;
using System.Diagnostics;
using VisionAlgorithm.OpenSrc.PatternMatching;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Imaging.Matching;

public sealed record TemplateSearchOutput(ImmutableArray<CorrelationMatch> Matches, int Windows, bool Truncated,
    double ReadMilliseconds, double TotalMilliseconds, bool MatchLimitExceeded = false);
