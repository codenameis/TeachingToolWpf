using VisionGeometry;
namespace VisionCanvas.Core.Geometry;

public static class HandleGeometry
{
    public const double DefaultScale = 1;
    public const double MinimumScale = .5;
    public const double MaximumScale = 3;
    public const double ResizeHitRadiusDip = 9;
    public const double RotationHitRadiusDip = 10;

    /// <summary>ROI 상단 경계로부터 회전 핸들 중심까지의 기본 오프셋 (28 DIP).</summary>
    public const double RotationHandleOffsetDip = 28.0;

    /// <summary>리사이즈 시 ROI 최소 폭/높이를 보장하기 위한 최소 여유 한계 (0.01px).</summary>
    public const double MinimumDimensionMarginPixels = 0.01;

    public static void ValidateScale(double scale)
    {
        if (!double.IsFinite(scale) || scale < MinimumScale || scale > MaximumScale)
        {
            throw new ArgumentOutOfRangeException(nameof(scale), "핸들 배율은 0.5~3 사이여야 합니다.");
        }
    }

    public static void CopyResizeHandlesTo(RoiGeometry rect, ViewportTransform view, Span<PointD> destination)
    {
        if (destination.Length < 8)
        {
            throw new ArgumentException("버퍼 크기는 최소 8이어야 합니다.", nameof(destination));
        }

        var corners = rect.FrameCorners.Select(view, static (v, p) => v.ImageToScreen(p));
        destination[0] = corners.A;
        destination[1] = (corners.A + corners.B) / 2;
        destination[2] = corners.B;
        destination[3] = (corners.B + corners.C) / 2;
        destination[4] = corners.C;
        destination[5] = (corners.C + corners.D) / 2;
        destination[6] = corners.D;
        destination[7] = (corners.D + corners.A) / 2;
    }

    public static IReadOnlyList<PointD> ResizeHandles(RoiGeometry rect, ViewportTransform view)
    {
        var handles = new PointD[8];
        CopyResizeHandlesTo(rect, view, handles);
        return handles;
    }

    public static PointD RotationHandle(RoiGeometry rect, ViewportTransform view, double scale = DefaultScale)
        => view.ImageToScreen(rect.ToWorld(new(0, -rect.Height / 2))) + new PointD(0, -RotationHandleOffsetDip * scale).Rotate(rect.Angle);

    public static RoiGeometry Resize(RoiGeometry original, int handle, PointD imagePoint)
    {
        var point = original.ToLocal(imagePoint);
        var left = -original.Width / 2;
        var right = original.Width / 2;
        var top = -original.Height / 2;
        var bottom = original.Height / 2;
        if (handle is 0 or 6 or 7)
        {
            left = Math.Min(point.X, right - MinimumDimensionMarginPixels);
        }

        if (handle is 2 or 3 or 4)
        {
            right = Math.Max(point.X, left + MinimumDimensionMarginPixels);
        }

        if (handle is 0 or 1 or 2)
        {
            top = Math.Min(point.Y, bottom - MinimumDimensionMarginPixels);
        }

        if (handle is 4 or 5 or 6)
        {
            bottom = Math.Max(point.Y, top + MinimumDimensionMarginPixels);
        }

        if (original.Shape.Kind == RoiShapeKind.Circle)
        {
            // 반대쪽 핸들을 고정하고 지름을 바꾼다. 원은 타원으로 변하지 않는다.
            var size = ComputeCircleDiameter(handle, left, right, top, bottom);
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
        return original.WithFrame(original.ToWorld(new((left + right) / 2, (top + bottom) / 2)), Math.Max(MinimumDimensionMarginPixels, right - left), Math.Max(MinimumDimensionMarginPixels, bottom - top), original.Angle);
    }

    private static double ComputeCircleDiameter(int handle, double left, double right, double top, double bottom)
    {
        double rawSize;
        if (handle is 1 or 5)
        {
            rawSize = bottom - top;
        }
        else if (handle is 3 or 7)
        {
            rawSize = right - left;
        }
        else
        {
            rawSize = Math.Min(right - left, bottom - top);
        }

        return Math.Max(MinimumDimensionMarginPixels, rawSize);
    }
}
