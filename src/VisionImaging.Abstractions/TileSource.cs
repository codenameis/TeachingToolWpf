using System.Buffers;

namespace VisionImaging.Abstractions;

public enum PixelKind
{
    Gray8, Gray16, Rgba32, Bgra32
}
public readonly record struct TileKey(int Level, int X, int Y);
public sealed record ImageInfo(string Key, string Name, int Width, int Height, PixelKind Pixels, int Levels)
{
    public const int TileSize = 512;

    public int BytesPerPixel => Pixels switch { PixelKind.Gray8 => 1, PixelKind.Gray16 => 2, _ => 4 };

    public static int CalculateLevelCount(int width, int height)
    {
        var count = 1;
        while (Math.Max(width, height) > TileSize)
        {
            width = (width + 1) / 2;
            height = (height + 1) / 2;
            count++;
        }
        return count;
    }
}

/// <summary>A source is owned by the image worker. Reads and disposal must not overlap.</summary>
public interface ITileSource : IDisposable
{
    ImageInfo Info
    {
        get;
    }
    PixelBuffer Read(TileKey key, CancellationToken cancellationToken);
}

/// <summary>One owner transfers or disposes this lease. GPU upload copies before releasing it.</summary>
public sealed class PixelBuffer : IDisposable
{
    private byte[]? _buffer;

    public int Width
    {
        get;
    }

    public int Height
    {
        get;
    }

    public int Stride
    {
        get;
    }

    public PixelKind Kind
    {
        get;
    }

    public int Length => checked(Stride * Height);

    public byte[] Bytes => _buffer ?? throw new ObjectDisposedException(nameof(PixelBuffer));

    public Span<byte> Span => Bytes.AsSpan(0, Length);

    public PixelBuffer(int width, int height, PixelKind kind)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        Width = width;
        Height = height;
        Kind = kind;
        Stride = checked(width * (kind == PixelKind.Gray8 ? 1 : kind == PixelKind.Gray16 ? 2 : 4));
        _buffer = ArrayPool<byte>.Shared.Rent(Length);
    }

    public void Dispose()
    {
        var owned = Interlocked.Exchange(ref _buffer, null);
        if (owned is not null)
        {
            ArrayPool<byte>.Shared.Return(owned);
        }
    }
}
