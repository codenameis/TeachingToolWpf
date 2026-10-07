using System.Collections.Immutable;

namespace VisionCanvas.Rendering;

/// <summary>렌더 스레드가 현재 계층의 역할만 보관한다. 확대 단계마다 문서 트리를 재조회하지 않는다.</summary>
internal sealed class RoiRoleLookup
{
    private readonly Dictionary<Guid, uint> _roles = [];
    private WeakReference<object>? _version;

    // Dictionary의 bucket/entry와 여유를 포함한 보수적 추정. 문서나 ROI 객체는 보관하지 않는다.
    public long EstimatedBytes => (long)_roles.EnsureCapacity(0) * 48;

    public void Update(ImmutableDictionary<Guid, ImmutableArray<Guid>> children)
    {
        if (_version is { } cached && cached.TryGetTarget(out var previous) && ReferenceEquals(previous, children))
        {
            return;
        }

        _roles.Clear();
        _roles.EnsureCapacity(children.Count + children.Values.Sum(siblings => siblings.Length));
        foreach (var siblings in children.Values)
        {
            foreach (var id in siblings)
            {
                _roles[id] = (uint)RoiRole.Child;
            }
        }
        // 중첩 부모는 자식 역할보다 우선한다.
        foreach (var (id, siblings) in children)
        {
            if (!siblings.IsEmpty)
            {
                _roles[id] = (uint)RoiRole.Parent;
            }
        }
        if (_roles.Count < _roles.EnsureCapacity(0) / 4)
        {
            _roles.TrimExcess();
        }
        _version = new(children);
    }

    public uint Resolve(Guid id) => _roles.GetValueOrDefault(id, (uint)RoiRole.Default);
}
