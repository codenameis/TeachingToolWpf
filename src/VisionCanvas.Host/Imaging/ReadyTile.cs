using System.Collections.Concurrent;
using VisionImaging.Abstractions;

namespace VisionCanvas.Imaging;

public sealed record ReadyTile(long Generation, TileKey Key, PixelBuffer Pixels);
