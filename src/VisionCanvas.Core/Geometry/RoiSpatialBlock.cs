using System.Collections.Immutable;
using VisionCanvas.Core.Editing;
using VisionGeometry;

namespace VisionCanvas.Core.Geometry;

/// <summary>Geometry-only immutable block; metadata is read from the current document by ID.</summary>
public sealed class RoiSpatialBlock(object identity, BoundsD bounds, int count, Func<double, IEnumerable<(Roi? Roi, BoundsD Bounds, int Count)>> enumerate, long shapeBytes = 0)
{
    public long ShapeBytes { get; } = shapeBytes;

    public object Identity { get; } = identity;

    public BoundsD Bounds { get; } = bounds;

    public int Count { get; } = count;

    public IEnumerable<(Roi? Roi, BoundsD Bounds, int Count)> Detail(double aggregateSize) => enumerate(aggregateSize);
}
