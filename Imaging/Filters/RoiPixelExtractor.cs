using OpenCvSharp;
using TeachingToolWpf.Core.Geometry;
using TeachingToolWpf.Core.Imaging;
using RotatedRect = TeachingToolWpf.Core.Geometry.RotatedRect;

namespace TeachingToolWpf.Imaging.Filters;

/// <summary>Reads only the bounding tiles at preview resolution, then rectifies a rotated ROI.</summary>
internal static class RoiPixelExtractor
{
    public static Mat FromTiles(ITileSource source, RotatedRect roi, int width, int height, CancellationToken token)
    {
        var ratio = Math.Max(roi.Width / width, roi.Height / height);
        var level = Math.Clamp((int)Math.Floor(Math.Log2(Math.Max(1, ratio))), 0, source.Info.Levels - 1);
        var factor = Math.Pow(2, level);
        var bounds = roi.Bounds;
        var sourceWidth = (int)Math.Ceiling(source.Info.Width / factor);
        var sourceHeight = (int)Math.Ceiling(source.Info.Height / factor);
        var left = Math.Clamp((int)Math.Floor(bounds.Left / factor) - 1, 0, sourceWidth - 1);
        var top = Math.Clamp((int)Math.Floor(bounds.Top / factor) - 1, 0, sourceHeight - 1);
        var right = Math.Clamp((int)Math.Ceiling(bounds.Right / factor) + 1, left + 1, sourceWidth);
        var bottom = Math.Clamp((int)Math.Ceiling(bounds.Bottom / factor) + 1, top + 1, sourceHeight);
        using var patch = new Mat(bottom - top, right - left, OpenCvRoiFilter.Type(source.Info.Pixels), Scalar.All(0));
        for (var y = top / 512; y <= (bottom - 1) / 512; y++)
        for (var x = left / 512; x <= (right - 1) / 512; x++)
        {
            token.ThrowIfCancellationRequested();
            using var tile = source.Read(new(level, x, y), token);
            using var wrapped = OpenCvRoiFilter.Wrap(tile);
            var x0 = Math.Max(left, x * 512); var y0 = Math.Max(top, y * 512);
            var w = Math.Min(right, x * 512 + tile.Width) - x0;
            var h = Math.Min(bottom, y * 512 + tile.Height) - y0;
            using var from = new Mat(wrapped, new Rect(x0 - x * 512, y0 - y * 512, w, h));
            using var to = new Mat(patch, new Rect(x0 - left, y0 - top, w, h));
            from.CopyTo(to);
        }
        token.ThrowIfCancellationRequested();
        return Rectify(patch, roi, width, height, factor, new(left, top));
    }

    public static Mat FromFrame(PixelBuffer pixels, RotatedRect roi, int width, int height)
    {
        using var source = OpenCvRoiFilter.Wrap(pixels);
        return Rectify(source, roi, width, height, 1, new());
    }

    private static Mat Rectify(Mat source, RotatedRect roi, int width, int height, double factor, PointD origin)
    {
        // Map destination pixel centers to source pixel centers; geometry uses image edge coordinates.
        var a = roi.ToWorld(new(-roi.Width / 2 + roi.Width / (2 * width), -roi.Height / 2 + roi.Height / (2 * height))) / factor - origin - new PointD(.5, .5);
        var dx = new PointD(roi.Width / width / factor, 0).Rotate(roi.Angle);
        var dy = new PointD(0, roi.Height / height / factor).Rotate(roi.Angle);
        using var matrix = new Mat(2, 3, MatType.CV_64FC1);
        matrix.Set(0, 0, dx.X); matrix.Set(0, 1, dy.X); matrix.Set(0, 2, a.X);
        matrix.Set(1, 0, dx.Y); matrix.Set(1, 1, dy.Y); matrix.Set(1, 2, a.Y);
        var output = new Mat();
        try
        {
            Cv2.WarpAffine(source, output, matrix, new(width, height), InterpolationFlags.Linear | InterpolationFlags.WarpInverseMap, BorderTypes.Replicate);
            return output;
        }
        catch { output.Dispose(); throw; }
    }
}
