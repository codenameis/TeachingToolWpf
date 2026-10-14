using VisionCanvas.Core.Editing;

namespace VisionCanvas.Rendering;

/// <summary>렌더 스레드가 현재 계층의 역할만 보관한다. 확대 단계마다 문서 트리를 재조회하지 않는다.</summary>
internal sealed class RoiRoleLookup
{
    private readonly Dictionary<Guid, uint> _roles = [];
    private WeakReference<object>? _version;

    // Dictionary의 bucket/entry와 여유를 포함한 보수적 추정. 문서나 ROI 객체는 보관하지 않는다.
    public long EstimatedBytes => (long)_roles.EnsureCapacity(0) * 48;

    public void Update(DocumentState document)
    {
        var levels = document.Snapshot.GroupLevels;
        if (_version is { } cached && cached.TryGetTarget(out var previous) && ReferenceEquals(previous, levels))
        {
            return;
        }

        _roles.Clear();
        _roles.EnsureCapacity(levels.Count);
        // 중첩 부모는 자식 역할보다 우선한다.
        foreach (var (id, level) in levels)
        {
            _roles[id] = (uint)(level switch
            {
                1 => RoiRole.Parent,
                2 => RoiRole.Module,
                _ => RoiRole.Root
            });
        }
        if (_roles.Count < _roles.EnsureCapacity(0) / 4)
        {
            _roles.TrimExcess();
        }
        _version = new(levels);
    }

    public uint Resolve(Guid id) => _roles.GetValueOrDefault(id, (uint)RoiRole.Child);
}
