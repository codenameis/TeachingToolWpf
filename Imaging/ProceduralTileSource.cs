using TeachingToolWpf.Core.Imaging;

namespace TeachingToolWpf.Imaging;

/// <summary>Deterministic, full-resolution sample surface. No source-sized allocation.</summary>
public sealed class ProceduralTileSource : ITileSource
{
    public ImageInfo Info
    {
        get;
    }

    public ProceduralTileSource(int width = 8192, int height = 8192)
        => Info = new($"sample:{width}:{height}", $"검사 샘플 · {width:N0} × {height:N0}", width, height, PixelKind.Rgba32, TilePlanner.LevelCount(width, height));

    public PixelBuffer Read(TileKey key, CancellationToken cancellationToken)
    {
        var factor = 1 << key.Level;
        var levelWidth = (Info.Width + factor - 1) / factor;
        var levelHeight = (Info.Height + factor - 1) / factor;
        var left = key.X * ImageInfo.TileSize;
        var top = key.Y * ImageInfo.TileSize;
        var result = new PixelBuffer(Math.Min(512, levelWidth - left), Math.Min(512, levelHeight - top), Info.Pixels);
        try
        {
            for (var y = 0;
y < result.Height;
y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (var x = 0;
x < result.Width;
x++)
                {
                    var wx = (left + x) * factor;
                    var wy = (top + y) * factor;
                    var cellX = wx % 2048;
                    var cellY = wy % 2048;
                    var edge = Math.Min(cellX, 2048 - cellX) < 24 || Math.Min(cellY, 2048 - cellY) < 24;
                    var pad = cellX > 360 && cellX < 1680 && cellY > 640 && cellY < 1400;
                    var pin = cellX > 250 && cellX < 1800 && cellX % 180 < 80 && ((cellY > 490 && cellY < 640) || (cellY > 1400 && cellY < 1550));
                    var trace = Math.Abs(cellX - cellY) < 12 || Math.Abs(cellX + cellY - 2048) < 12;
                    var noise = (((wx * 13) ^ (wy * 7)) & 7);
                    var i = (y * result.Stride) + (x * 4);
                    result.Bytes[i] = (byte)((pad ? 36 : pin ? 195 : edge ? 20 : trace ? 95 : 40) + noise);
                    result.Bytes[i + 1] = (byte)((pad ? 43 : pin ? 181 : edge ? 37 : trace ? 133 : 83) + noise);
                    result.Bytes[i + 2] = (byte)((pad ? 57 : pin ? 135 : edge ? 44 : trace ? 116 : 83) + noise);
                    result.Bytes[i + 3] = 255;
                }
            }
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
    }
}
