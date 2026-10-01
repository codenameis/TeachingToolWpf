using TeachingToolWpf.Core.Editing;

namespace TeachingToolWpf.Core.Geometry;

/// <summary>Image-space constraints shared by pointer, commands and numeric property edits.</summary>
public static class RoiImageBounds
{
    public static PointD Clamp(PointD point, ImageReference? image) => image is null ? point
        : new(Math.Clamp(point.X, 0, image.Width), Math.Clamp(point.Y, 0, image.Height));

    public static bool ContainsAll(DocumentState state) => state.Image is not { } image ||
        state.Rois.Values.All(r => r.Geometry.Corners.All(p =>
            p.X >= -1e-7 && p.Y >= -1e-7 && p.X <= image.Width + 1e-7 && p.Y <= image.Height + 1e-7));

    public static RotatedRect ClipNew(RotatedRect rect, ImageReference? image)
    {
        if (image is null) return rect;
        var bounds = rect.Bounds;
        if (rect.Angle == 0)
        {
            var a = Clamp(new(bounds.Left, bounds.Top), image);
            var b = Clamp(new(bounds.Right, bounds.Bottom), image);
            if (b.X - a.X < .01 || b.Y - a.Y < .01) throw new ArgumentException("ROI는 이미지 안에 그려 주세요.");
            return new((a + b) / 2, b.X - a.X, b.Y - a.Y, 0);
        }
        var scale = Math.Min(1, Math.Min(image.Width / bounds.Width, image.Height / bounds.Height));
        var halfWidth = bounds.Width * scale / 2; var halfHeight = bounds.Height * scale / 2;
        return new(new(Math.Clamp(rect.Center.X, halfWidth, image.Width - halfWidth),
            Math.Clamp(rect.Center.Y, halfHeight, image.Height - halfHeight)), rect.Width * scale, rect.Height * scale, rect.Angle);
    }

    internal static RotatedRect Interpolate(RotatedRect from, RotatedRect to, double fraction)
    {
        var angle = (to.Angle - from.Angle + 540) % 360 - 180;
        return new(from.Center + (to.Center - from.Center) * fraction,
            from.Width + (to.Width - from.Width) * fraction, from.Height + (to.Height - from.Height) * fraction,
            from.Angle + angle * fraction);
    }
}
