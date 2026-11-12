using System.Buffers;

namespace VisionImaging.Abstractions;

public readonly record struct TileKey(int Level, int X, int Y);
