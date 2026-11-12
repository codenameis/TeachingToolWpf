using VisionAlgorithm.OpenSrc.PatternMatching;
using VisionAlgorithm.OpenSrc.ShapeDetection;

namespace VisionInspection.Cli;

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
    public ImagePoint[][] InnerContours { get; init; } = [];
    public ImagePoint[][] AdditionalContours { get; init; } = [];
    public RefinementSummary? Refinement
    {
        get; init;
    }
}
