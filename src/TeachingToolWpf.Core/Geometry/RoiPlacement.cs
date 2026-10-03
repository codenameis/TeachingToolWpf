namespace TeachingToolWpf.Core.Geometry;

/// <summary>Places a useful starter rectangle inside the visible part of an image.</summary>
public static class RoiPlacement
{
    public static RoiGeometry InView(int imageWidth, int imageHeight, ViewportTransform view, double width, double height)
    {
        if (imageWidth <= 0 || imageHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(imageWidth));
        }

        var a = view.ScreenToImage(new(0, 0));
        var b = view.ScreenToImage(new(width, height));
        var left = Math.Clamp(a.X, 0, imageWidth);
        var top = Math.Clamp(a.Y, 0, imageHeight);
        var right = Math.Clamp(b.X, 0, imageWidth);
        var bottom = Math.Clamp(b.Y, 0, imageHeight);
        if (right - left < 1 || bottom - top < 1)
        {
            left = top = 0;
            right = imageWidth;
            bottom = imageHeight;
        }
        var roiWidth = Math.Max(.01, (right - left) * .32);
        var roiHeight = Math.Max(.01, Math.Min((bottom - top) * .32, roiWidth * .75));
        return new(new((left + right) / 2, (top + bottom) / 2), roiWidth, roiHeight, 0);
    }
}
