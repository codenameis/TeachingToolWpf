using System.Text.Json.Serialization;
using VisionAlgorithm.OpenSrc.PatternMatching;
using VisionAlgorithm.OpenSrc.ShapeDetection;
using VisionGeometry;

namespace VisionInspection.Cli;

public sealed record InspectionRecipe
{
    public int SchemaVersion { get; init; } = 1;
    public string Method { get; init; } = "paper";
    public string? TrainingImage
    {
        get; init;
    }
    public RoiDefinition? TeachingRoi
    {
        get; init;
    }
    public string InspectionImage { get; init; } = "";
    public RoiDefinition? SearchRoi
    {
        get; init;
    }
    public TrainingOptions TrainingOptions { get; init; } = new();
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public ShapeSearchOptions SearchOptions { get; init; } = new() { MaximumMatches = 256, ClutterWeight = 1 };
    public PrimitiveDetectionOptions PrimitiveOptions { get; init; } = new();
    public double TimeoutSeconds
    {
        get; init;
    }
}
