using System.Text.Json;
using System.Text.Json.Serialization;
using VisionCanvas.Core.Editing;

namespace TeachingToolWpf.Models;

/// <summary>
/// Serializable teaching &amp; inspection recipe model containing ROI geometries, ordered pre-inspection filter pipelines,
/// and active inspection category/algorithm parameters.
/// </summary>
public sealed record TeachingRecipeDocument
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public string RecipeVersion { get; init; } = "2.0";

    public string SavedAtUtc { get; init; } = DateTime.UtcNow.ToString("O");

    public string ActiveTeachingCategory { get; init; } = "Pattern";

    public bool PreFilterBeforeInspection { get; init; } = true;

    public List<FilterSettings> SessionFilterPipeline { get; init; } = [];

    public List<TeachingRecipeRoiEntry> Rois { get; init; } = [];

    public string ToJson() => JsonSerializer.Serialize(this, SerializerOptions);

    public static TeachingRecipeDocument? FromJson(string json) =>
        JsonSerializer.Deserialize<TeachingRecipeDocument>(json, SerializerOptions);
}
