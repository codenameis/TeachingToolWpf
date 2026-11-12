using System.Collections.Immutable;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace VisionCanvas.Rendering;

public sealed record TextRasterBatch(PixelBuffer Pixels, ImmutableDictionary<string, BoundsD> Regions) : IDisposable
{
    public void Dispose() => Pixels.Dispose();
}
