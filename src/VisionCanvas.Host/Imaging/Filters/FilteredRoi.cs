using System.Collections.Immutable;
using VisionCanvas.Core.Editing;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace VisionCanvas.Imaging.Filters;

/// <summary>Immutable by ownership: bytes are published once and never mutated after construction.</summary>
public sealed record FilteredRoi(Guid Id, RoiGeometry Geometry, int Width, int Height, byte[] Gray8, byte[]? Mask = null)
{
    public FilterExecutionReport? Execution
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
