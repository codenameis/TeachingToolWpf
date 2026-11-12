using System.Collections.Immutable;
using VisionCanvas.Core.Editing;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace VisionCanvas.Imaging.Filters;

public sealed record FilterPreviewResult(IReadOnlyList<FilteredRoi> Regions, double Milliseconds);
