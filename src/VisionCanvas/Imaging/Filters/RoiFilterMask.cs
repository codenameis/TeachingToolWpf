using TeachingToolWpf.Core.Geometry;

namespace TeachingToolWpf.Imaging.Filters;

internal static class RoiFilterMask
{
    public static byte[]? Create(RoiGeometry geometry, int width, int height, CancellationToken token)
    {
        if (geometry.Shape.Kind == RoiShapeKind.Rectangle)
        {
            return null;
        }

        var mask = new byte[checked(width * height)];
        for (var y = 0;
y < height;
y++)
        {
            token.ThrowIfCancellationRequested();
            for (var x = 0;
x < width;
x++)
            {
                var local = new PointD((((x + .5) / width) - .5) * geometry.Width, (((y + .5) / height) - .5) * geometry.Height);
                if (geometry.Shape.ContainsLocal(local, geometry.Width, geometry.Height, 0))
                {
                    mask[(y * width) + x] = 255;
                }
            }
        }
        return mask;
    }
}
