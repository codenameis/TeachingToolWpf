using System.Text.Json.Serialization;

namespace TeachingToolWpf.Fluent2.Tests.OcrBenchmark;

/// <summary>
/// Represents a pixel-disjoint real font-specimen ROI used exclusively to teach a golden font
/// for a specific <see cref="OcrPrintGroupSpec"/> in Stage 3 OCV evaluation.
/// </summary>
public sealed record OcrOcvSpecimenEntry(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("imageFile")] string ImageFile,
    [property: JsonPropertyName("imageWidth")] int ImageWidth,
    [property: JsonPropertyName("imageHeight")] int ImageHeight,
    [property: JsonPropertyName("centerX")] double CenterX,
    [property: JsonPropertyName("centerY")] double CenterY,
    [property: JsonPropertyName("width")] double Width,
    [property: JsonPropertyName("height")] double Height,
    [property: JsonPropertyName("groundTruth")] string GroundTruth);
