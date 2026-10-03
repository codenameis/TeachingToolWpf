namespace TeachingToolWpf.Core.Geometry;

public static class HandleGeometry
{
    public static IReadOnlyList<PointD> ResizeHandles(RoiGeometry rect, ViewportTransform view)
    {
        var corners = rect.FrameCorners.Select(view.ImageToScreen).ToArray();
        return [corners[0], (corners[0] + corners[1]) / 2, corners[1], (corners[1] + corners[2]) / 2,
            corners[2], (corners[2] + corners[3]) / 2, corners[3], (corners[3] + corners[0]) / 2];
    }

    public static PointD RotationHandle(RoiGeometry rect, ViewportTransform view)
        => view.ImageToScreen(rect.ToWorld(new(0, -rect.Height / 2))) + new PointD(0, -28).Rotate(rect.Angle);

    public static RoiGeometry Resize(RoiGeometry original, int handle, PointD imagePoint)
    {
        var point = original.ToLocal(imagePoint);
        var left = -original.Width / 2;
        var right = original.Width / 2;
        var top = -original.Height / 2;
        var bottom = original.Height / 2;
        if (handle is 0 or 6 or 7)
        {
            left = Math.Min(point.X, right - .01);
        }

        if (handle is 2 or 3 or 4)
        {
            right = Math.Max(point.X, left + .01);
        }

        if (handle is 0 or 1 or 2)
        {
            top = Math.Min(point.Y, bottom - .01);
        }

        if (handle is 4 or 5 or 6)
        {
            bottom = Math.Max(point.Y, top + .01);
        }

        if (original.Shape.Kind == RoiShapeKind.Circle)
        {
            // 반대쪽 핸들을 고정하고 지름을 바꾼다. 원은 타원으로 변하지 않는다.
            var size = handle is 1 or 5 ? bottom - top : handle is 3 or 7 ? right - left : Math.Min(right - left, bottom - top);
            size = Math.Max(.01, size);
            if (handle is 0 or 6 or 7)
            {
                left = right - size;
            }
            else if (handle is 2 or 3 or 4)
            {
                right = left + size;
            }
            else
            {
                left = -size / 2;
                right = size / 2;
            }
            if (handle is 0 or 1 or 2)
            {
                top = bottom - size;
            }
            else if (handle is 4 or 5 or 6)
            {
                bottom = top + size;
            }
            else
            {
                top = -size / 2;
                bottom = size / 2;
            }
            return original.WithFrame(original.ToWorld(new((left + right) / 2, (top + bottom) / 2)), size, size, original.Angle);
        }
        return original.WithFrame(original.ToWorld(new((left + right) / 2, (top + bottom) / 2)), Math.Max(.01, right - left), Math.Max(.01, bottom - top), original.Angle);
    }
}
