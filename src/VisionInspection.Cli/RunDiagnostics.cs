using VisionAlgorithm.OpenSrc.PatternMatching;
using VisionAlgorithm.OpenSrc.ShapeDetection;

namespace VisionInspection.Cli;

public sealed record RunDiagnostics
{
    public PrimitivePreprocessingDiagnostics? PrimitivePreprocessing
    {
        get; init;
    }
    public double TotalMilliseconds
    {
        get; init;
    }
    public double TrainingMilliseconds
    {
        get; init;
    }
    public double SearchMilliseconds
    {
        get; init;
    }
    public double ReadMilliseconds
    {
        get; init;
    }
    public int Windows
    {
        get; init;
    }
    public bool Truncated
    {
        get; init;
    }
    public bool MatchLimitExceeded
    {
        get; init;
    }
    public int ClippedContours
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
    public ShapeSearchDiagnostics[] Search { get; init; } = [];
}
