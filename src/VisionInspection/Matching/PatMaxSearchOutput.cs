using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using VisionAlgorithm.OpenSrc.PatMax;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Imaging.Matching;

public sealed record PatMaxSearchOutput(
    ImmutableArray<PatMaxSearchMatch> Matches,
    double ReadMilliseconds,
    double SearchMilliseconds,
    double TotalMilliseconds,
    bool MatchLimitExceeded);
