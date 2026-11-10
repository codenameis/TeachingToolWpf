using System;
using System.Threading;
using VisionImaging.Abstractions;

namespace ToolBlock.Simulator.Services;

/// <summary>
/// In-memory <see cref="ITileSource"/> backed by a Gray8 pixel buffer
/// for zero-latency rendering inside <c>VisionCanvas.Skia</c>.
/// </summary>
public sealed class ToolBlockMemoryTileSource : ITileSource
{
    private const int RgbaChannelCount = 4;
    private readonly byte[] _grayBuffer;
    private readonly int _stride;

    public ImageInfo Info { get; }

    public ToolBlockMemoryTileSource(ImageReference reference, byte[] grayBuffer, int width, int height, int stride)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(grayBuffer);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, width);

        _grayBuffer = grayBuffer;
        _stride = stride;
        Info = new ImageInfo(
            reference.Key,
            string.IsNullOrWhiteSpace(reference.Name) ? "ToolBlock Live Scene" : reference.Name,
            width,
            height,
            PixelKind.Rgba32,
            ImageInfo.CalculateLevelCount(width, height));
    }

    public PixelBuffer Read(TileKey key, CancellationToken cancellationToken)
        => ReadTilePixels(key, PixelKind.Rgba32, cancellationToken);

    public PixelBuffer ReadOriginalGray8(int tileX, int tileY, CancellationToken cancellationToken)
        => ReadTilePixels(new TileKey(0, tileX, tileY), PixelKind.Gray8, cancellationToken);

    public void Dispose()
    {
    }

    private PixelBuffer ReadTilePixels(TileKey key, PixelKind kind, CancellationToken token)
    {
        if (key.Level < 0 || key.Level >= Info.Levels || key.X < 0 || key.Y < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(key));
        }

        int factor = 1 << key.Level;
        int levelWidth = (Info.Width + factor - 1) / factor;
        int levelHeight = (Info.Height + factor - 1) / factor;
        int left = checked(key.X * ImageInfo.TileSize);
        int top = checked(key.Y * ImageInfo.TileSize);
        if (left >= levelWidth || top >= levelHeight)
        {
            throw new ArgumentOutOfRangeException(nameof(key));
        }

        int tileW = Math.Min(ImageInfo.TileSize, levelWidth - left);
        int tileH = Math.Min(ImageInfo.TileSize, levelHeight - top);
        var buffer = new PixelBuffer(tileW, tileH, kind);
        try
        {
            int srcW = Info.Width;
            int srcH = Info.Height;
            ReadOnlySpan<byte> master = _grayBuffer;

            for (int y = 0; y < tileH; y++)
            {
                token.ThrowIfCancellationRequested();
                int srcY = Math.Min(srcH - 1, (top + y) * factor);
                int bytesPerPixel = kind == PixelKind.Gray8 ? 1 : RgbaChannelCount;
                Span<byte> dstRow = buffer.Span.Slice(y * buffer.Stride, tileW * bytesPerPixel);

                if (kind == PixelKind.Gray8)
                {
                    if (factor == 1)
                    {
                        master.Slice((srcY * _stride) + left, tileW).CopyTo(dstRow);
                    }
                    else
                    {
                        for (int x = 0; x < tileW; x++)
                        {
                            int srcX = Math.Min(srcW - 1, (left + x) * factor);
                            dstRow[x] = master[(srcY * _stride) + srcX];
                        }
                    }
                }
                else
                {
                    for (int x = 0; x < tileW; x++)
                    {
                        int srcX = Math.Min(srcW - 1, (left + x) * factor);
                        byte g = master[(srcY * _stride) + srcX];
                        int idx = x * RgbaChannelCount;
                        dstRow[idx] = g;
                        dstRow[idx + 1] = g;
                        dstRow[idx + 2] = g;
                        dstRow[idx + 3] = byte.MaxValue;
                    }
                }
            }

            return buffer;
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
    }
}
