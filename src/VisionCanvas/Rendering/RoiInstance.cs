using System.Numerics;
using System.Runtime.InteropServices;
using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Geometry;
using VisionGeometry;

namespace VisionCanvas.Rendering;

[StructLayout(LayoutKind.Sequential)]
internal readonly record struct RoiInstance(Vector2 Center, Vector2 AxisX, Vector2 AxisY, uint Summary, uint Padding = 0)
{
    public const int SizeInBytes = 32;
    public const int RoleSizeInBytes = sizeof(uint);
    private const uint RectangleInstance = 0;
    private const uint SummaryInstance = 1;
    private const uint EdgeInstance = 2;
    private const uint CircleInstance = 3;
    // 보관 예산만 제한한다. 꼭짓점 수가 많은 도형도 전부 처리한 뒤 큰 임시 버퍼를 놓는다.
    internal const int MaximumRetainedInstanceCount = 1024 * 1024 / SizeInBytes;
    private const int MaximumRetainedDictionaryCapacity = 4096;

    private static int InstanceCount(RoiGeometry geometry) => geometry.Shape.Kind is RoiShapeKind.Rectangle or RoiShapeKind.Circle ? 1 : geometry.Shape.Vertices.Length;

    public static RoiInstance[] Pack(RoiSpatialBlock block, double aggregateSize)
        => Build(block, aggregateSize, null, includeGeometry: true).Instances;

    public static (RoiInstance[] Instances, uint[] Roles) PackWithRoles(RoiSpatialBlock block, double aggregateSize, DocumentState document)
        => Build(block, aggregateSize, document, includeGeometry: true);

    public static uint[] ResolveRoles(RoiSpatialBlock block, double aggregateSize, DocumentState document, RoiRoleLookup? lookup = null)
        => Build(block, aggregateSize, document, includeGeometry: false, lookup).Roles;

    internal static bool PackInto(RoiSpatialBlock block, double aggregateSize, DocumentState document,
        List<RoiInstance> instances, List<uint> roles, Dictionary<RoiGeometry, int> unique, RoiRoleLookup? lookup = null)
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
        // 기본 공간 블록은 최대2048 ROI다. 더 큰 외부 블록의 사전 용량을 계속 보관하지 않는다.
        if (unique.EnsureCapacity(0) > MaximumRetainedDictionaryCapacity)
        {
            unique.TrimExcess();
        }
    }

    private static (RoiInstance[] Instances, uint[] Roles) Build(RoiSpatialBlock block, double aggregateSize, DocumentState? document, bool includeGeometry, RoiRoleLookup? lookup = null)
    {
        var instances = includeGeometry ? new List<RoiInstance>() : null;
        var roles = document is null ? null : new List<uint>();
        var unique = new Dictionary<RoiGeometry, int>();
        Fill(block, aggregateSize, document, instances, roles, unique, lookup);
        return (instances?.ToArray() ?? [], roles?.ToArray() ?? []);
    }

    private static bool Fill(RoiSpatialBlock block, double aggregateSize, DocumentState? document,
        List<RoiInstance>? instances, List<uint>? roles, Dictionary<RoiGeometry, int> unique, RoiRoleLookup? lookup = null)
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
                        instances.Add(new(Vector(item.Bounds.Center - origin), horizontalAxis, verticalAxis, SummaryInstance));
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
                var next = i + 1 == vertices.Length ? first
                    : shape.ToWorld(new(vertices[i + 1].X * shape.Width, vertices[i + 1].Y * shape.Height));
                instances.Add(new(Vector(point - origin), Vector(next - point), default, EdgeInstance));
                point = next;
            }
            return;
        }
        var angle = shape.Angle * Math.PI / 180;
        var cosine = Math.Cos(angle);
        var sine = Math.Sin(angle);
        var horizontalAxis = new Vector2((float)(cosine * shape.Width / 2), (float)(sine * shape.Width / 2));
        var verticalAxis = new Vector2((float)(-sine * shape.Height / 2), (float)(cosine * shape.Height / 2));
        instances.Add(new(Vector(shape.Center - origin), horizontalAxis, verticalAxis,
            shape.Shape.Kind == RoiShapeKind.Circle ? CircleInstance : RectangleInstance));
    }

    private static Vector2 Vector(PointD point) => new((float)point.X, (float)point.Y);
}

/// <summary>Hierarchy metadata changes independently of retained geometry and palette constants.</summary>
internal sealed class RoiRoleCache
{
    // 작은 비가시 블록 하나가 이전 문서 전체의 계층 배열을 붙잡지 않도록 한다.
    private WeakReference<object>? _hierarchyVersion;
    private WeakReference<object>? _previousVersion;
    private uint[]? _previousValues;

    public uint[] Values
    {
        get;
        private set;
    } = [];

    public RoiRoleCache()
    {
    }

    public RoiRoleCache(DocumentState document, uint[] values)
    {
        Values = values;
        _hierarchyVersion = new(document.Snapshot.GroupLevels);
    }

    public bool Refresh(RoiSpatialBlock block, double aggregateSize, DocumentState document, int instanceCount, RoiRoleLookup? lookup = null)
    {
        var version = document.Snapshot.GroupLevels;
        if (_hierarchyVersion is { } cached && cached.TryGetTarget(out var previous) && ReferenceEquals(previous, version))
        {
            return false;
        }

        // 한 번의 Undo/Redo 전환은 정점/중복 윤곽을 다시 순회하지 않는다. 두 역할 배열만 보관한다.
        if (_previousVersion is { } alternate && alternate.TryGetTarget(out var other) && ReferenceEquals(other, version))
        {
            (_hierarchyVersion, _previousVersion) = (_previousVersion, _hierarchyVersion);
            (Values, _previousValues) = (_previousValues!, Values);
            return true;
        }

        if (version.IsEmpty && Values.Length == instanceCount)
        {
            // 계층이 없어져도 실제 ROI와 축소 요약의 역할은 구별한다.
            var roles = new uint[instanceCount];
            for (var index = 0; index < roles.Length; index++)
            {
                roles[index] = Values[index] == (uint)RoiRole.Default ? (uint)RoiRole.Default : (uint)RoiRole.Child;
            }
            return RefreshValues(version, roles, instanceCount);
        }

        return RefreshValues(version, RoiInstance.ResolveRoles(block, aggregateSize, document, lookup), instanceCount);
    }

    // 배열 소유권을 받는다. 이미 패킹한 역할을 재사용해 계층을 두 번 순회하지 않는다.
    public bool RefreshPacked(DocumentState document, uint[] values, int instanceCount)
        => RefreshValues(document.Snapshot.GroupLevels, values, instanceCount);

    private bool RefreshValues(object version, uint[] next, int instanceCount)
    {
        if (next.Length != instanceCount)
        {
            throw new InvalidOperationException("ROI 역할과 형상 인스턴스 개수가 다릅니다.");
        }

        // 계층 버전은 갱신하되 실제 역할이 같으면 GPU 복사를 생략한다.
        if (Values.AsSpan().SequenceEqual(next))
        {
            _hierarchyVersion = new(version);
            return false;
        }

        _previousVersion = _hierarchyVersion;
        _previousValues = Values;
        _hierarchyVersion = new(version);
        Values = next;
        return true;
    }
}
