// File: src/VisionAlgorithm/OpenSrc/Common/ReadOnlyImageView2D.cs
using System.Runtime.CompilerServices;

namespace VisionAlgorithm.OpenSrc.Common;

/// <summary>
/// Zero-allocation 2D read-only image view bundling pixel buffer, dimensions, and row stride
/// to eliminate 4-parameter <c>(pixels, width, height, stride)</c> data clumps across vision kernels.
/// </summary>
public readonly ref struct ReadOnlyImageView2D<T>
    where T : unmanaged
{
    private readonly bool _isInitialized;

    public ReadOnlySpan<T> Pixels { get; }
    public ReadOnlySpan<T> Buffer => Pixels;
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public bool IsValid => _isInitialized && Width > 0 && Height > 0 && Stride >= Width;

    public ReadOnlyImageView2D(ReadOnlySpan<T> pixels, int width, int height)
        : this(pixels, width, height, width)
    {
    }

    public ReadOnlyImageView2D(ReadOnlySpan<T> pixels, int width, int height, int stride)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(width, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(height, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, width);

        long requiredElements = ((long)(height - 1) * stride) + width;
        if (pixels.Length < requiredElements)
        {
            throw new ArgumentException("Pixel buffer is smaller than the specified dimensions and stride.", nameof(pixels));
        }

        Pixels = pixels;
        Width = width;
        Height = height;
        Stride = stride;
        _isInitialized = true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<T> GetRowSpan(int row)
        => Pixels.Slice(row * Stride, Width);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<T> RowSpan(int y)
        => Pixels.Slice(y * Stride, Width);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void EnsureValid()
    {
        if (!IsValid)
        {
            throw new InvalidOperationException($"{nameof(ReadOnlyImageView2D<T>)} was not initialized via its constructor.");
        }
    }
}
