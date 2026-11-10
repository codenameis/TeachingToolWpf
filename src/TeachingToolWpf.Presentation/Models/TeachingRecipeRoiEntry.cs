using VisionCanvas.Core.Editing;
using VisionGeometry;

namespace TeachingToolWpf.Models;

/// <summary>
/// Serializable ROI entry inside a <see cref="TeachingRecipeDocument"/>.
/// </summary>
public sealed record TeachingRecipeRoiEntry(
    string Name,
    RoiShapeKind Kind,
    double CenterX,
    double CenterY,
    double Width,
    double Height,
    double AngleDegrees,
    double SearchMarginPixels,
    FilterSettings PrimaryFilter,
    List<FilterSettings> FilterPipeline);
