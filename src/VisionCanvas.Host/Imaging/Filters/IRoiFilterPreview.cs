using System.Collections.Immutable;
using VisionCanvas.Core.Editing;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace VisionCanvas.Imaging.Filters;

/// <summary>One caller at a time; no UI access or source edits; cancellation discards incomplete results.</summary>
public interface IRoiFilterPreview
{
    Task<FilterPreviewResult> ProcessAsync(FilterRequest request, CancellationToken cancellationToken);
}
