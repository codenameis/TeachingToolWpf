using System.Collections.Immutable;
using VisionCanvas.Core.Editing;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace VisionCanvas.Imaging.Filters;

public sealed record FilterStageSummary(string Filter, string Device, string Reason);
