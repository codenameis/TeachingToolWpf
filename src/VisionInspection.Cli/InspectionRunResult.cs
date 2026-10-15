using VisionAlgorithm.OpenSrc.PatternMatching;

namespace VisionInspection.Cli;

public sealed record InspectionRunResult
{
    public int SchemaVersion { get; init; } = 1;
    public string Status { get; init; } = "failed";
    public string Method { get; init; } = "";
    public bool Incomplete
    {
        get; init;
    }
    public string? CancellationReason
    {
        get; init;
    }
    public string? Error
    {
        get; init;
    }
    public ImageSummary? InspectionImage
    {
        get; init;
    }
    public ImageSummary? TrainingImage
    {
        get; init;
    }
    public DetectionResult[] Matches { get; init; } = [];
    public RunDiagnostics Diagnostics { get; init; } = new();

    public int ExitCode => Status switch
    {
        "completed" => 0,
        "incomplete" => 2,
        "cancelled" => 3,
        _ => 1
    };
}

public sealed record ImageSummary(string Path, int Width, int Height, string Version);

public sealed record DetectionResult
{
    public string Kind { get; init; } = "pattern";
    public ImagePoint Center
    {
        get; init;
    }
    public double AngleRadians
    {
        get; init;
    }
    public double Scale { get; init; } = 1;
    public double Score
    {
        get; init;
    }
    public double? DirectionScore
    {
        get; init;
    }
    public double? Support
    {
        get; init;
    }
    public double? ClutterRatio
    {
        get; init;
    }
    public double? ClutterPenaltyRatio
    {
        get; init;
    }
    public double? AreaPixels
    {
        get; init;
    }
    public string ContourSource { get; init; } = "transformedTeachingRoi";
    public ImagePoint[] Contour { get; init; } = [];
    public RefinementSummary? Refinement
    {
        get; init;
    }
}

public sealed record RefinementSummary(string Status, double? InitialResidual, double? FinalResidual,
    int Iterations, int Correspondences, bool AngleObservable);

public sealed record RunDiagnostics
{
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
    public SearchDiagnostics[] Search { get; init; } = [];
}
