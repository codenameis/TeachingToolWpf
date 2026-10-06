using VisionGeometry;
using VisionImaging.Abstractions;

namespace VisionCanvas.Imaging;

public static class TilePlanner
{
    public static IReadOnlyList<TileKey> Visible(ImageInfo image, ViewportTransform view, double width, double height, double dpi)
    {
        if (width <= 0 || height <= 0 || view.Scale <= 0 || dpi <= 0)
        {
            return [];
        }

        var level = Math.Clamp((int)Math.Floor(Math.Log2(1 / (view.Scale * dpi))), 0, image.Levels - 1);
        var factor = 1 << level;
        var tileSpan = (double)ImageInfo.TileSize * factor;
        var first = view.ScreenToImage(new PointD(0, 0));
        var last = view.ScreenToImage(new PointD(width, height));
        var columns = (int)Math.Ceiling(image.Width / tileSpan);
        var rows = (int)Math.Ceiling(image.Height / tileSpan);
        var x0 = Math.Clamp((int)Math.Floor(first.X / tileSpan), 0, columns);
        var y0 = Math.Clamp((int)Math.Floor(first.Y / tileSpan), 0, rows);
        var x1 = Math.Clamp((int)Math.Ceiling(last.X / tileSpan), 0, columns);
        var y1 = Math.Clamp((int)Math.Ceiling(last.Y / tileSpan), 0, rows);
        var result = new List<TileKey>();
        for (var y = y0; y < y1; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                result.Add(new(level, x, y));
            }
        }

        return result;
    }
}
