using System.Numerics;
using System.Runtime.InteropServices;
using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Geometry;
using VisionGeometry;

namespace VisionCanvas.Rendering.Abstractions;

/// <summary>불변 공간 블록 상대 좌표로 패킹되는 32바이트 ROI GPU 인스턴스.</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct RoiInstance(Vector2 Center, Vector2 AxisX, Vector2 AxisY, RoiInstanceKind Kind, uint Padding = 0)
{
    public const int SizeInBytes = 32;
    public const int RoleSizeInBytes = sizeof(uint);
    // 보관 예산만 제한한다. 꼭짓점 수가 많은 도형도 전부 처리한 뒤 큰 임시 버퍼를 놓는다.
    internal const int MaximumRetainedInstanceCount = 1024 * 1024 / SizeInBytes;
    private const int MaximumRetainedDictionaryCapacity = 4096;

    private static int InstanceCount(RoiGeometry geometry)
        => geometry.Shape.Kind is RoiShapeKind.Rectangle or RoiShapeKind.Circle ? 1 : geometry.Shape.Vertices.Length;

    public static RoiInstance[] Pack(RoiSpatialBlock block, double aggregateSize)
    {
        ArgumentNullException.ThrowIfNull(block);
        return Build(block, aggregateSize, null, includeGeometry: true).Instances;
    }

    public static (RoiInstance[] Instances, uint[] Roles) PackWithRoles(RoiSpatialBlock block, double aggregateSize, DocumentState document)
    {
        ArgumentNullException.ThrowIfNull(block);
        ArgumentNullException.ThrowIfNull(document);
        return Build(block, aggregateSize, document, includeGeometry: true);
    }

    public static uint[] ResolveRoles(RoiSpatialBlock block, double aggregateSize, DocumentState document, RoiRoleLookup? lookup = null)
    {
        ArgumentNullException.ThrowIfNull(block);
        ArgumentNullException.ThrowIfNull(document);
        return Build(block, aggregateSize, document, includeGeometry: false, lookup).Roles;
    }

    internal static bool PackInto(
        RoiSpatialBlock block,
        double aggregateSize,
        DocumentState document,
        List<RoiInstance> instances,
        List<uint> roles,
        Dictionary<RoiGeometry, int> unique,
        RoiRoleLookup? lookup = null)
        => Fill(block, aggregateSize, document, instances, roles, unique, lookup);

    internal static void ReleasePackingBuffers(List<RoiInstance> instances, List<uint> roles, Dictionary<RoiGeometry, int> unique)
    {
        instances.Clear();
        roles.Clear();
        unique.Clear();
        if (instances.Capacity > MaximumRetainedInstanceCount)
        {
            instances.Capacity = 0;
        }

        if (roles.Capacity > MaximumRetainedInstanceCount)
        {
            roles.Capacity = 0;
        }

        // 기본 공간 블록은 최대 2048 ROI다. 더 큰 외부 블록의 사전 용량을 계속 보관하지 않는다.
        if (unique.EnsureCapacity(0) > MaximumRetainedDictionaryCapacity)
        {
            unique.TrimExcess();
        }
    }

    private static (RoiInstance[] Instances, uint[] Roles) Build(
        RoiSpatialBlock block,
        double aggregateSize,
        DocumentState? document,
        bool includeGeometry,
        RoiRoleLookup? lookup = null)
    {
        var instances = includeGeometry ? new List<RoiInstance>() : null;
        var roles = document is null ? null : new List<uint>();
        var unique = new Dictionary<RoiGeometry, int>();
        Fill(block, aggregateSize, document, instances, roles, unique, lookup);
        return (instances?.ToArray() ?? [], roles?.ToArray() ?? []);
    }

    private static bool Fill(
        RoiSpatialBlock block,
        double aggregateSize,
        DocumentState? document,
        List<RoiInstance>? instances,
        List<uint>? roles,
        Dictionary<RoiGeometry, int> unique,
        RoiRoleLookup? lookup = null)
    {
        instances?.Clear();
        roles?.Clear();
        unique.Clear();
        var origin = block.Bounds.Center;
        var hasSummary = false;
        if (document is not null)
        {
            lookup?.Update(document);
        }

        // 최초 업로드는 형상과 역할을 같은 순회로 만든다. 역할만 바뀌면 형상을 다시 계산하지 않는다.
        try
        {
            foreach (var item in block.Detail(aggregateSize))
            {
                if (item.Roi is not { } roi)
                {
                    hasSummary = true;
                    if (instances is not null)
                    {
                        var horizontalAxis = new Vector2((float)(item.Bounds.Width / 2), 0);
                        var verticalAxis = new Vector2(0, (float)(item.Bounds.Height / 2));
                        instances.Add(new(Vector(item.Bounds.Center - origin), horizontalAxis, verticalAxis, RoiInstanceKind.Summary));
                    }

                    roles?.Add((uint)RoiRole.Default);
                    continue;
                }

                var role = lookup?.Resolve(roi.Id) ?? (document is null ? 0 : (uint)RoiStyleRoles.Resolve(document, roi.Id));
                if (unique.TryGetValue(roi.Geometry, out var index))
                {
                    // 같은 윤곽에는 상위 그룹 역할을 우선 적용한다.
                    if (roles is not null)
                    {
                        for (var i = 0; i < InstanceCount(roi.Geometry); i++)
                        {
                            roles[index + i] = Math.Max(roles[index + i], role);
                        }
                    }

                    continue;
                }

                unique.Add(roi.Geometry, roles?.Count ?? instances!.Count);
                if (instances is not null)
                {
                    AppendGeometry(instances, roi.Geometry, origin);
                }

                if (roles is not null)
                {
                    for (var i = 0; i < InstanceCount(roi.Geometry); i++)
                    {
                        roles.Add(role);
                    }
                }
            }

            return hasSummary;
        }
        finally
        {
            // scratch의 사전 키가 이전 ROI 형상과 폴리곤 꼭짓점 배열을 붙잡지 않도록 한다.
            unique.Clear();
        }
    }

    private static void AppendGeometry(List<RoiInstance> instances, RoiGeometry shape, PointD origin)
    {
        if (shape.Shape.Kind is RoiShapeKind.Triangle or RoiShapeKind.Polygon)
        {
            var vertices = shape.Shape.Vertices;
            var first = shape.ToWorld(new(vertices[0].X * shape.Width, vertices[0].Y * shape.Height));
            var point = first;
            for (var i = 0; i < vertices.Length; i++)
            {
                var next = i + 1 == vertices.Length
                    ? first
                    : shape.ToWorld(new(vertices[i + 1].X * shape.Width, vertices[i + 1].Y * shape.Height));
                instances.Add(new(Vector(point - origin), Vector(next - point), default, RoiInstanceKind.Edge));
                point = next;
            }

            return;
        }

        var angle = shape.Angle * GeometryConstants.DegreesToRadians;
        var cosine = Math.Cos(angle);
        var sine = Math.Sin(angle);
        var horizontalAxis = new Vector2((float)(cosine * shape.Width / 2), (float)(sine * shape.Width / 2));
        var verticalAxis = new Vector2((float)(-sine * shape.Height / 2), (float)(cosine * shape.Height / 2));
        instances.Add(new(
            Vector(shape.Center - origin),
            horizontalAxis,
            verticalAxis,
            shape.Shape.Kind == RoiShapeKind.Circle ? RoiInstanceKind.Circle : RoiInstanceKind.Rectangle));
    }

    private static Vector2 Vector(PointD point) => new((float)point.X, (float)point.Y);
}
