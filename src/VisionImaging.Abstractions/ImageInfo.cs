using System.Buffers;

namespace VisionImaging.Abstractions;

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
