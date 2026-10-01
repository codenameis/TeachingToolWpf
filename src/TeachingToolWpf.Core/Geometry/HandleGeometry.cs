namespace TeachingToolWpf.Core.Geometry;

public static class HandleGeometry
{
    public static IReadOnlyList<PointD> ResizeHandles(RotatedRect rect, ViewportTransform view)
    {
        var corners = rect.Corners.Select(view.ImageToScreen).ToArray();
        return [corners[0], (corners[0] + corners[1]) / 2, corners[1], (corners[1] + corners[2]) / 2,
            corners[2], (corners[2] + corners[3]) / 2, corners[3], (corners[3] + corners[0]) / 2];
    }
    public static PointD RotationHandle(RotatedRect rect, ViewportTransform view)
        => view.ImageToScreen(rect.ToWorld(new(0, -rect.Height / 2))) + new PointD(0, -28).Rotate(rect.Angle);
    public static RotatedRect Resize(RotatedRect original, int handle, PointD imagePoint)
    {
        var point = original.ToLocal(imagePoint);
        var left = -original.Width / 2; var right = original.Width / 2;
        var top = -original.Height / 2; var bottom = original.Height / 2;
        if (handle is 0 or 6 or 7) left = Math.Min(point.X, right - .01);
        if (handle is 2 or 3 or 4) right = Math.Max(point.X, left + .01);
        if (handle is 0 or 1 or 2) top = Math.Min(point.Y, bottom - .01);
        if (handle is 4 or 5 or 6) bottom = Math.Max(point.Y, top + .01);
        return new(original.ToWorld(new((left + right) / 2, (top + bottom) / 2)), right - left, bottom - top, original.Angle);
    }
}
