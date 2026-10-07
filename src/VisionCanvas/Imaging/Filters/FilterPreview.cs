using IndustrialVision.Filters.Core;
using VisionCanvas.Core.Editing;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace VisionCanvas.Imaging.Filters;

public sealed record FilterRequest(ImageReference Image, bool IsLive, IReadOnlyList<Roi> Rois, RoiTransformPreview? Transform = null)
{
    public FilterSettings? OverrideSettings
    {
        get; init;
    }
    public FilterSettings Settings(Roi roi) => OverrideSettings ?? roi.Filter;
    // Evaluated by the single filter worker after debounce, never materialized per pointer sample.
    public RoiGeometry Geometry(Roi roi) => Transform?.Geometry(roi) ?? roi.Geometry;
}
/// <summary>Immutable by ownership: bytes are published once and never mutated after construction.</summary>
public sealed record FilteredRoi(Guid Id, RoiGeometry Geometry, int Width, int Height, byte[] Gray8, byte[]? Mask = null)
{
    public ExecutionReport? Execution
    {
        get; init;
    }
    public double PreparationMilliseconds
    {
        get; init;
    }
    public bool ReusedPlan
    {
        get; init;
    }
}
public sealed record FilterPreviewResult(IReadOnlyList<FilteredRoi> Regions, double Milliseconds);

/// <summary>One caller at a time; no UI access or source edits; cancellation discards incomplete results.</summary>
public interface IRoiFilterPreview
{
    Task<FilterPreviewResult> ProcessAsync(FilterRequest request, CancellationToken cancellationToken);
}
