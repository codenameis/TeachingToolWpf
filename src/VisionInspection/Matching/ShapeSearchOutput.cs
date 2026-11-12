using System.Collections.Immutable;
using System.Diagnostics;
using VisionAlgorithm;
using VisionAlgorithm.OpenSrc.PatternMatching;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Imaging.Matching;

public sealed record ShapeSearchOutput(ImmutableArray<ShapeMatch> Matches, int Windows, bool Truncated,
    double ReadMilliseconds, double TotalMilliseconds, ImmutableArray<ShapeSearchDiagnostics> Diagnostics, bool MatchLimitExceeded = false)
{
    public ShapeSearchPhases? Phases
    {
        get; init;
    }
    public ImagePreprocessingDiagnostics? Preprocessing
    {
        get; init;
    }

    public CandidateScoringDiagnostics? CandidateScoring
    {
        get; init;
    }
}
