using System.Buffers;

namespace VisionImaging.Abstractions;

/// <summary>A source is owned by the image worker. Reads and disposal must not overlap.</summary>
public interface ITileSource : IDisposable
{
    ImageInfo Info
    {
        get;
    }
    PixelBuffer Read(TileKey key, CancellationToken cancellationToken);
}
