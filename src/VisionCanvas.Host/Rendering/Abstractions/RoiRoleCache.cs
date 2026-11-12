using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Geometry;

namespace VisionCanvas.Rendering.Abstractions;

/// <summary>보존된 형상 및 팔레트 상수와 독립적으로 변경되는 ROI 계층 역할 캐시.</summary>
public sealed class RoiRoleCache
{
    // 작은 비가시 블록 하나가 이전 문서 전체의 계층 배열을 붙잡지 않도록 한다.
    private WeakReference<object>? _hierarchyVersion;
    private WeakReference<object>? _previousVersion;
    private uint[]? _previousValues;

    public uint[] Values { get; private set; } = [];

    public RoiRoleCache()
    {
    }

    public RoiRoleCache(DocumentState document, uint[] values)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(values);
        Values = values;
        _hierarchyVersion = new(document.Snapshot.GroupLevels);
    }

    public bool Refresh(RoiSpatialBlock block, double aggregateSize, DocumentState document, int instanceCount, RoiRoleLookup? lookup = null)
    {
        ArgumentNullException.ThrowIfNull(block);
        ArgumentNullException.ThrowIfNull(document);
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
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(values);
        return RefreshValues(document.Snapshot.GroupLevels, values, instanceCount);
    }

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
