using System.Numerics;
using System.Runtime.InteropServices;
using TeachingToolWpf.Core.Geometry;

namespace TeachingToolWpf.Rendering;

[StructLayout(LayoutKind.Sequential)]
internal readonly record struct RoiInstance(Vector2 Center, Vector2 AxisX, Vector2 AxisY, uint Summary, uint Padding = 0)
{
    public static RoiInstance[] Pack(RoiSpatialBlock block, double aggregateSize)
    {
        var result = new List<RoiInstance>(); var unique = new HashSet<RotatedRect>();
        var origin = block.Bounds.Center;
        foreach (var item in block.Detail(aggregateSize))
        {
            if (item.Roi is { } roi)
            {
                var shape = roi.Geometry;
                if (!unique.Add(shape)) continue;
                var angle = shape.Angle * Math.PI / 180; var c = Math.Cos(angle); var s = Math.Sin(angle);
                result.Add(new(Vector(shape.Center - origin), new((float)(c * shape.Width / 2), (float)(s * shape.Width / 2)),
                    new((float)(-s * shape.Height / 2), (float)(c * shape.Height / 2)), 0));
            }
            else result.Add(new(Vector(item.Bounds.Center - origin), new((float)(item.Bounds.Width / 2), 0), new(0, (float)(item.Bounds.Height / 2)), 1));
        }
        return result.ToArray();
    }
    private static Vector2 Vector(PointD point) => new((float)point.X, (float)point.Y);
}
