// File: Common/GrayImageView2D.cs
using System;

namespace TeachingToolWpf.Fluent2.Common;

public readonly ref struct GrayImageView2D
{
    private readonly ReadOnlySpan<byte> _buffer;

    public int Width { get; }

    public int Height { get; }

    public int RowStride { get; }

    public bool IsInitialized { get; }

    private GrayImageView2D(ReadOnlySpan<byte> buffer, int width, int height, int rowStride)
    {
        _buffer = buffer;
        Width = width;
        Height = height;
        RowStride = rowStride;
        IsInitialized = true;
    }

    public ReadOnlySpan<byte> Buffer => _buffer;

    public static GrayImageView2D Create(ReadOnlySpan<byte> buffer, int width, int height, int rowStride)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (rowStride < width)
        {
            throw new ArgumentOutOfRangeException(nameof(rowStride), "RowStride must be greater than or equal to image width.");
        }

        long requiredBytes = ((long)(height - 1) * rowStride) + width;
        if (buffer.Length < requiredBytes)
        {
            throw new ArgumentException("Buffer length is smaller than the strided 2D image footprint.", nameof(buffer));
        }

        return new GrayImageView2D(buffer, width, height, rowStride);
    }

    public ReadOnlySpan<byte> GetRowSpan(int y)
    {
        if (!IsInitialized)
        {
            throw new InvalidOperationException("GrayImageView2D must be created via GrayImageView2D.Create.");
        }

        if ((y < 0) || (y >= Height))
        {
            throw new ArgumentOutOfRangeException(nameof(y));
        }

        int rowOffset = y * RowStride;
        return _buffer.Slice(rowOffset, Width);
    }
}
