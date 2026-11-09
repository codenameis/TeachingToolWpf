// File: Common/RoiPixelBounds.cs
using System;

namespace TeachingToolWpf.Fluent2.Common;

public readonly record struct RoiPixelBounds
{
    private const int MinimumInteriorRadius = 1; // [Domain Why]: 3x3 Sobel operator requires a 1-pixel border around interior sample points.
    private const int MinimumTeachableSpan = (MinimumInteriorRadius * 2) + 1;

    public int X { get; }

    public int Y { get; }

    public int Width { get; }

    public int Height { get; }

    public bool IsInitialized { get; }

    private RoiPixelBounds(int x, int y, int width, int height)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
        IsInitialized = true;
    }

    public int Right => X + Width;

    public int Bottom => Y + Height;

    public int InteriorWidth => Math.Max(0, Width - (MinimumInteriorRadius * 2));

    public int InteriorHeight => Math.Max(0, Height - (MinimumInteriorRadius * 2));

    public static RoiPixelBounds Create(int x, int y, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(x);
        ArgumentOutOfRangeException.ThrowIfNegative(y);
        if (width < MinimumTeachableSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "ROI width must be at least 3 pixels for 3x3 Sobel gradient evaluation.");
        }

        if (height < MinimumTeachableSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(height), "ROI height must be at least 3 pixels for 3x3 Sobel gradient evaluation.");
        }

        return new RoiPixelBounds(x, y, width, height);
    }

    public bool IsWithin(int imageWidth, int imageHeight)
    {
        if (!IsInitialized)
        {
            return false;
        }

        return (X >= 0)
            && (Y >= 0)
            && (Width >= MinimumTeachableSpan)
            && (Height >= MinimumTeachableSpan)
            && (Right <= imageWidth)
            && (Bottom <= imageHeight);
    }
}
