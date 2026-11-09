using System.Collections.Immutable;
using VisionCanvas.Core.Alignment;
using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Geometry;
using VisionCanvas.Core.Interaction;
using VisionCanvas.Layers;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace VisionCanvas.Rendering;

public sealed record RenderScene(DocumentState Document, ImageInfo? Image, long Generation, double Width, double Height, double Dpi,
    InteractionOverlay Overlay, bool PointSampling = false, bool IsLive = false, VisionCanvas.Imaging.Filters.FilterPreviewResult? FilterPreview = null, RoiTransformPreview? Transform = null,
    RoiStylePalette? Styles = null, ResultOverlay? Inspection = null, RoiAlignmentMap? Alignment = null)
{
    public MinimapOptions MinimapOptions { get; init; } = MinimapOptions.Default;

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

    /// <summary>드래그 미리보기 또는 계층 보정(전체·모듈·컴포넌트)이 반영된 ROI의 현재 표시 기하를 반환한다.</summary>
    public RoiGeometry EffectiveGeometry(Roi roi)
    {
        ArgumentNullException.ThrowIfNull(roi);
        if (Transform is { } preview && preview.Selection.Affected.Contains(roi.Id))
        {
            return preview.Geometry(roi);
        }

        return Alignment is { HasAnyAlignment: true } alignment
            ? alignment.ApplyGeometry(roi)
            : roi.Geometry;
    }
}
