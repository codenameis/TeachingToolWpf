using VisionGeometry;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Imaging.Matching;

/// <summary>
/// Clamped integer crop window in source image pixel coordinates.
/// </summary>
internal readonly record struct MatchingCropWindow(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;

    public static MatchingCropWindow Expand(ImageReference image, BoundsD bounds, int padding, int alignment = 1)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentOutOfRangeException.ThrowIfNegative(padding);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(alignment);

        var rawLeft = (int)Math.Floor(bounds.Left) - padding;
        var rawTop = (int)Math.Floor(bounds.Top) - padding;
        var alignedLeft = alignment > 1 ? rawLeft / alignment * alignment : rawLeft;
        var alignedTop = alignment > 1 ? rawTop / alignment * alignment : rawTop;

        var left = Math.Max(0, alignedLeft);
        var top = Math.Max(0, alignedTop);
        var right = Math.Min(image.Width, (int)Math.Ceiling(bounds.Right) + padding);
        var bottom = Math.Min(image.Height, (int)Math.Ceiling(bounds.Bottom) + padding);
        return new MatchingCropWindow(left, top, right, bottom);
    }
}
