using System.Buffers;
using System.Runtime.CompilerServices;
using VisionKernels;

namespace VisionAlgorithm.OpenSrc.PatMax;

/// <summary>
/// Read-only view of a single pyramid level with explicit stride and scale factor relative to the base image.
/// </summary>
public readonly struct PyramidLevelView
{
    private readonly byte[]? _buffer;

    public int LevelIndex { get; }
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public float ScaleFactor { get; }
    public bool HasPixels => _buffer is not null;

    public ReadOnlySpan<byte> Pixels => _buffer is not null ? _buffer.AsSpan(0, Height * Stride) : ReadOnlySpan<byte>.Empty;

    internal PyramidLevelView(byte[]? buffer, int levelIndex, int width, int height, int stride, float scaleFactor)
    {
        _buffer = buffer;
        LevelIndex = levelIndex;
        Width = width;
        Height = height;
        Stride = stride;
        ScaleFactor = scaleFactor;
    }
}
