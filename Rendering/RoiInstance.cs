using System.Numerics;
using System.Runtime.InteropServices;
using TeachingToolWpf.Core.Editing;
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
    public static uint[] ResolveRoles(RoiSpatialBlock block, double aggregateSize, DocumentState document)
    {
        var result = new List<uint>(); var unique = new Dictionary<RotatedRect, int>();
        // Match Pack's first-occurrence order exactly, including neutral summary slots.
        foreach (var item in block.Detail(aggregateSize))
        {
            if (item.Roi is not { } roi) { result.Add((uint)RoiRole.Default); continue; }
            var role = (uint)RoiStyleRoles.Resolve(document, roi.Id);
            if (unique.TryGetValue(roi.Geometry, out var index)) result[index] = Math.Max(result[index], role);
            else { unique.Add(roi.Geometry, result.Count); result.Add(role); }
        }
        return result.ToArray();
    }
    private static Vector2 Vector(PointD point) => new((float)point.X, (float)point.Y);
}

/// <summary>Hierarchy metadata changes independently of retained geometry and palette constants.</summary>
internal sealed class RoiRoleCache
{
    // An off-screen block must not keep an entire historical document's hierarchy alive.
    private WeakReference<object>? hierarchyVersion;
    public uint[] Values { get; private set; } = [];
    public bool Refresh(RoiSpatialBlock block, double aggregateSize, DocumentState document, int instanceCount)
    {
        var version = document.Snapshot.Children;
        if (hierarchyVersion is { } cached && cached.TryGetTarget(out var previous) && ReferenceEquals(previous, version)) return false;
        var next = RoiInstance.ResolveRoles(block, aggregateSize, document);
        if (next.Length != instanceCount) throw new InvalidOperationException("ROI 역할과 형상 인스턴스 개수가 다릅니다.");
        if (hierarchyVersion is null) hierarchyVersion = new(version);
        else hierarchyVersion.SetTarget(version);
        if (Values.AsSpan().SequenceEqual(next)) return false;
        Values = next;
        return true;
    }
}
