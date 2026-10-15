namespace CpuMatching.Core;

public readonly record struct EdgePoint(double X, double Y, double Angle, double Strength);

public static class Edges
{
    public static EdgePoint[] Extract(GrayImage image, double centerX, double centerY, int maximum = 128)
    {
        var candidates = new List<EdgePoint>();
        var gradients = Gradients(image);
        for (var y = 2; y < image.Height - 2; y++)
        {
            for (var x = 2; x < image.Width - 2; x++)
            {
                var index = (y * image.Width) + x;
                var (gx, gy) = gradients[index];
                var strength = Math.Sqrt((gx * gx) + (gy * gy));
                if (strength < 12)
                {
                    continue;
                }
                candidates.Add(new EdgePoint(x - centerX, y - centerY, Math.Atan2(gy, gx), strength));
            }
        }
        var selected = new List<EdgePoint>();
        foreach (var point in candidates.OrderByDescending(point => point.Strength))
        {
            if (!selected.Any(other => ((other.X - point.X) * (other.X - point.X)) + ((other.Y - point.Y) * (other.Y - point.Y)) < 4))
            {
                selected.Add(point);
                if (selected.Count == maximum)
                {
                    break;
                }
            }
        }
        return selected.ToArray();
    }

    public static (double X, double Y)[] Gradients(GrayImage image)
    {
        var result = new (double, double)[image.Width * image.Height];
        for (var y = 1; y < image.Height - 1; y++)
        {
            for (var x = 1; x < image.Width - 1; x++)
            {
                var gx = (image[x + 1, y - 1] + (2 * image[x + 1, y]) + image[x + 1, y + 1] -
                    image[x - 1, y - 1] - (2 * image[x - 1, y]) - image[x - 1, y + 1]) / 8.0;
                var gy = (image[x - 1, y + 1] + (2 * image[x, y + 1]) + image[x + 1, y + 1] -
                    image[x - 1, y - 1] - (2 * image[x, y - 1]) - image[x + 1, y - 1]) / 8.0;
                result[(y * image.Width) + x] = (gx, gy);
            }
        }
        return result;
    }

    public static float Bilinear(float[] map, int width, int height, double x, double y)
    {
        if (x < 0 || y < 0 || x >= width - 1 || y >= height - 1)
        {
            return 0;
        }
        var ix = (int)x;
        var iy = (int)y;
        var dx = (float)(x - ix);
        var dy = (float)(y - iy);
        var index = (iy * width) + ix;
        return ((1 - dy) * (((1 - dx) * map[index]) + (dx * map[index + 1]))) +
            (dy * (((1 - dx) * map[index + width]) + (dx * map[index + width + 1])));
    }
}
