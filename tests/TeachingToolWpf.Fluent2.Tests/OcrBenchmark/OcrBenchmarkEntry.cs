using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace TeachingToolWpf.Fluent2.Tests.OcrBenchmark;

/// <summary>
/// Represents a single labeled OCR/OCV ROI benchmark entry loaded from <c>labels.json</c> (schemaVersion 2).
/// </summary>
public sealed record OcrBenchmarkEntry(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("imageFile")] string ImageFile,
    [property: JsonPropertyName("imageWidth")] int ImageWidth,
    [property: JsonPropertyName("imageHeight")] int ImageHeight,
    [property: JsonPropertyName("centerX")] double CenterX,
    [property: JsonPropertyName("centerY")] double CenterY,
    [property: JsonPropertyName("width")] double Width,
    [property: JsonPropertyName("height")] double Height,
    [property: JsonPropertyName("angleDeg")] double AngleDeg,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("split")] string Split,
    [property: JsonPropertyName("printGroup")] string PrintGroup,
    [property: JsonPropertyName("printGroupRationale")] string PrintGroupRationale,
    [property: JsonPropertyName("groundTruthLines")] IReadOnlyList<string> GroundTruthLines)
{
    public string GroundTruthJoined => string.Join(" / ", GroundTruthLines);
}
