using System.Collections.Immutable;
using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Interaction;
using VisionCanvas.Layers;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace VisionCanvas.Rendering;

public sealed record RenderScene(DocumentState Document, ImageInfo? Image, long Generation, double Width, double Height, double Dpi,
    InteractionOverlay Overlay, bool PointSampling = false, bool IsLive = false, VisionCanvas.Imaging.Filters.FilterPreviewResult? FilterPreview = null, RoiTransformPreview? Transform = null,
    RoiStylePalette? Styles = null, ResultOverlay? Inspection = null)
{
    public RepeatedRoiGrid? RepeatedGrid
    {
        get; init;
    }
    public int? RepeatedSelection
    {
        get; init;
    }

    public bool ShowMasks { get; init; } = true;
    public int SelectedMaskIndex { get; init; } = -1;
    public bool MaskEditing
    {
        get; init;
    }

    internal ImmutableArray<LayerSnapshot> Layers { get; init; } = [];
    internal bool InspectionInLayer
    {
        get; init;
    }

    public RoiStylePalette RoiStyles => Styles ?? RoiStylePalette.Standard;
}
