using VisionGeometry;
namespace VisionCanvas.Core.Geometry;

/// <summary>Places a useful starter rectangle inside the visible part of an image.</summary>
public static class RoiPlacement
{
    public static RoiGeometry InParent(RoiGeometry parent, RoiShapeKind shape)
    {
        var outline = parent.Outline;
        var centers = new[] { parent.Center }.Concat(Enumerable.Range(0, outline.Length).Select(i =>
            (outline[i] + outline[(i + 1) % outline.Length] + outline[(i + 2) % outline.Length]) / 3));
        RoiGeometry? best = null;
        foreach (var center in centers)
        {
            if (!parent.Contains(center))
            {
                continue;
            }
            for (var scale = 1d / 3; scale >= .000001; scale /= 2)
            {
                var child = RoiGeometry.Create(shape, center, Math.Max(.01, parent.Width * scale), Math.Max(.01, parent.Height * scale));
                child = child.RotateAround(center, parent.Angle);
                if (best is not null && child.Area <= best.Area)
                {
                    break;
                }
                if (parent.Covers(child))
                {
                    best = child;
                    break;
                }
            }
            if (best is not null && center == parent.Center)
            {
                return best;
            }
        }
        return best ?? throw new InvalidOperationException("부모 안에 ROI를 추가할 공간이 부족합니다. 부모를 키우거나 직접 그려 주세요.");
    }

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
