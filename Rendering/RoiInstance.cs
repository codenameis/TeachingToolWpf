using System.Numerics;
using System.Runtime.InteropServices;
using TeachingToolWpf.Core.Editing;
using TeachingToolWpf.Core.Geometry;

namespace TeachingToolWpf.Rendering;

[StructLayout(LayoutKind.Sequential)]
internal readonly record struct RoiInstance(Vector2 Center, Vector2 AxisX, Vector2 AxisY, uint Summary, uint Padding = 0)
{
    public const int SizeInBytes = 32;
    public const int RoleSizeInBytes = sizeof(uint);
    private const uint RectangleInstance = 0;
    private const uint SummaryInstance = 1;

    public static RoiInstance[] Pack(RoiSpatialBlock block, double aggregateSize)
    {
        var result = new List<RoiInstance>();
        var unique = new HashSet<RotatedRect>();
        var origin = block.Bounds.Center;
        foreach (var item in block.Detail(aggregateSize))
        {
            if (item.Roi is { } roi)
            {
                var shape = roi.Geometry;
                if (!unique.Add(shape))
                    continue;
                var angle = shape.Angle * Math.PI / 180;
                var cosine = Math.Cos(angle);
                var sine = Math.Sin(angle);
                var horizontalAxis = new Vector2((float)(cosine * shape.Width / 2), (float)(sine * shape.Width / 2));
                var verticalAxis = new Vector2((float)(-sine * shape.Height / 2), (float)(cosine * shape.Height / 2));
                result.Add(new(Vector(shape.Center - origin), horizontalAxis, verticalAxis, RectangleInstance));
            }
            else
            {
                var horizontalAxis = new Vector2((float)(item.Bounds.Width / 2), 0);
                var verticalAxis = new Vector2(0, (float)(item.Bounds.Height / 2));
                result.Add(new(Vector(item.Bounds.Center - origin), horizontalAxis, verticalAxis, SummaryInstance));
            }
        }
        return result.ToArray();
    }
    public static uint[] ResolveRoles(RoiSpatialBlock block, double aggregateSize, DocumentState document)
    {
        var result = new List<uint>();
        var unique = new Dictionary<RotatedRect, int>();
        // 형상 버퍼와 같은 순서로 묶어야 역할 SRV의 인덱스가 일치한다. 요약은 중립 역할이다.
        foreach (var item in block.Detail(aggregateSize))
        {
            if (item.Roi is not { } roi)
            {
                result.Add((uint)RoiRole.Default);
                continue;
            }
            var role = (uint)RoiStyleRoles.Resolve(document, roi.Id);
            if (unique.TryGetValue(roi.Geometry, out var index))
            {
                // 같은 윤곽에는 부모 > 자식 > 일반 우선순위를 적용한다.
                result[index] = Math.Max(result[index], role);
            }
            else
            {
                unique.Add(roi.Geometry, result.Count);
                result.Add(role);
            }
        }
        return result.ToArray();
    }
    private static Vector2 Vector(PointD point) => new((float)point.X, (float)point.Y);
}

/// <summary>Hierarchy metadata changes independently of retained geometry and palette constants.</summary>
internal sealed class RoiRoleCache
{
    // 작은 비가시 블록 하나가 이전 문서 전체의 계층 배열을 붙잡지 않도록 한다.
    private WeakReference<object>? hierarchyVersion;
    public uint[] Values { get; private set; } = [];
    public bool Refresh(RoiSpatialBlock block, double aggregateSize, DocumentState document, int instanceCount)
    {
        var version = document.Snapshot.Children;
        if (hierarchyVersion is { } cached && cached.TryGetTarget(out var previous) && ReferenceEquals(previous, version))
            return false;
        var next = RoiInstance.ResolveRoles(block, aggregateSize, document);
        if (next.Length != instanceCount)
            throw new InvalidOperationException("ROI 역할과 형상 인스턴스 개수가 다릅니다.");

        if (hierarchyVersion is null)
            hierarchyVersion = new(version);
        else
            hierarchyVersion.SetTarget(version);

        // 계층 버전은 갱신하되 실제 역할이 같으면 GPU 복사를 생략한다.
        if (Values.AsSpan().SequenceEqual(next))
            return false;
        Values = next;
        return true;
    }
}
