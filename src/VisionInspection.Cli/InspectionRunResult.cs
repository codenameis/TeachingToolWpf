using VisionAlgorithm.OpenSrc.PatternMatching;
using VisionAlgorithm.OpenSrc.ShapeDetection;

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
