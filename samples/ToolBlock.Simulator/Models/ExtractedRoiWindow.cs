// File: samples/ToolBlock.Simulator/Models/ExtractedRoiWindow.cs
using System;
using VisionAlgorithm.OpenSrc.Common;

namespace ToolBlock.Simulator.Models;

/// <summary>
/// Represents a bounded Gray8 window extracted from either a disk pyramid tile source or an in-memory frame.
/// When <see cref="ScaleFactor"/> &gt; 1 (e.g., when a huge region or Whole Image on <c>ken32.png</c> is analyzed),
/// coordinates inside <see cref="Pixels"/> are downsampled by <see cref="ScaleFactor"/> (<c>2^Level</c>).
/// </summary>
public readonly struct ExtractedRoiWindow : IEquatable<ExtractedRoiWindow>
{
    public ExtractedRoiWindow(
        byte[] pixels,
        int width,
        int height,
        int stride,
        int worldX,
        int worldY,
        int worldWidth,
        int worldHeight,
        int scaleFactor)
    {
        Pixels = pixels ?? [];
        Width = Math.Max(0, width);
        Height = Math.Max(0, height);
        Stride = Math.Max(Width, stride);
        WorldX = worldX;
        WorldY = worldY;
        WorldWidth = Math.Max(0, worldWidth);
        WorldHeight = Math.Max(0, worldHeight);
        ScaleFactor = Math.Max(1, scaleFactor);
    }

    public byte[] Pixels { get; }
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public int WorldX { get; }
    public int WorldY { get; }
    public int WorldWidth { get; }
    public int WorldHeight { get; }
    public int ScaleFactor { get; }

    public bool IsValid => Pixels is not null && Width > 0 && Height > 0 && Pixels.Length >= Height * Stride;

    public ReadOnlyImageView2D<byte> AsView()
        => IsValid ? new ReadOnlyImageView2D<byte>(Pixels, Width, Height, Stride) : default;

    public bool Equals(ExtractedRoiWindow other) =>
        ReferenceEquals(Pixels, other.Pixels) &&
        Width == other.Width &&
        Height == other.Height &&
        Stride == other.Stride &&
        WorldX == other.WorldX &&
        WorldY == other.WorldY &&
        WorldWidth == other.WorldWidth &&
        WorldHeight == other.WorldHeight &&
        ScaleFactor == other.ScaleFactor;

    public override bool Equals(object? obj) => obj is ExtractedRoiWindow other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(Width, Height, Stride, WorldX, WorldY, WorldWidth, WorldHeight, ScaleFactor);

    public static bool operator ==(ExtractedRoiWindow left, ExtractedRoiWindow right) => left.Equals(right);

    public static bool operator !=(ExtractedRoiWindow left, ExtractedRoiWindow right) => !left.Equals(right);
}
