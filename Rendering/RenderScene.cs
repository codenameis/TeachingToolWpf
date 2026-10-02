using TeachingToolWpf.Core.Editing;
using TeachingToolWpf.Core.Geometry;
using TeachingToolWpf.Core.Imaging;
using TeachingToolWpf.Core.Interaction;

namespace TeachingToolWpf.Rendering;

public sealed record RenderScene(DocumentState Document, ImageInfo? Image, long Generation, double Width, double Height, double Dpi,
    InteractionOverlay Overlay, bool PointSampling = false, bool IsLive = false, Imaging.Filters.FilterPreviewResult? FilterPreview = null, RoiTransformPreview? Transform = null);
