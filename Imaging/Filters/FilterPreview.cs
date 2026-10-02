using TeachingToolWpf.Core.Editing;
using TeachingToolWpf.Core.Geometry;

namespace TeachingToolWpf.Imaging.Filters;

public sealed record FilterRequest(ImageReference Image, bool IsLive, IReadOnlyList<Roi> Rois, RoiTransformPreview? Transform = null)
{
    // Evaluated by the single filter worker after debounce, never materialized per pointer sample.
    public RotatedRect Geometry(Roi roi) => Transform?.Geometry(roi) ?? roi.Geometry;
}
/// <summary>Immutable by ownership: bytes are published once and never mutated after construction.</summary>
public sealed record FilteredRoi(Guid Id, RotatedRect Geometry, int Width, int Height, byte[] Gray8);
public sealed record FilterPreviewResult(IReadOnlyList<FilteredRoi> Regions, double Milliseconds);

/// <summary>One caller at a time; no UI/GPU access, no source edits, cancellation discards incomplete results.</summary>
public interface IRoiFilterPreview
{
    Task<FilterPreviewResult> ProcessAsync(FilterRequest request, CancellationToken cancellationToken);
}
