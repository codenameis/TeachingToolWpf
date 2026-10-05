using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Interaction;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace VisionCanvas.Rendering;

public sealed record RenderScene(DocumentState Document, ImageInfo? Image, long Generation, double Width, double Height, double Dpi,
    InteractionOverlay Overlay, bool PointSampling = false, bool IsLive = false, VisionCanvas.Imaging.Filters.FilterPreviewResult? FilterPreview = null, RoiTransformPreview? Transform = null,
    RoiStylePalette? Styles = null, ResultOverlay? Inspection = null)
{
    public RoiStylePalette RoiStyles => Styles ?? RoiStylePalette.Standard;
}
