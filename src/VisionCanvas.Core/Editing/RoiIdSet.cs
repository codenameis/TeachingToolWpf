using System.Collections;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;

namespace VisionCanvas.Core.Editing;

/// <summary>Immutable bulk membership with small persistent changes for Ctrl-click selection.</summary>
[CollectionBuilder(typeof(RoiIdSet), nameof(Create))]
public sealed class RoiIdSet : IReadOnlySet<Guid>
{
    private readonly FrozenSet<Guid> _items;
    private readonly ImmutableHashSet<Guid> _added;
    private readonly ImmutableHashSet<Guid> _removed;

    public static RoiIdSet Empty { get; } = new(FrozenSet<Guid>.Empty, [], []);
    public int Count => _items.Count + _added.Count - _removed.Count;
    public bool IsEmpty => Count == 0;

    private RoiIdSet(FrozenSet<Guid> items, ImmutableHashSet<Guid> added, ImmutableHashSet<Guid> removed)
    {
        _items = items;
        _added = added;
        _removed = removed;
    }

    public static RoiIdSet Create(ReadOnlySpan<Guid> ids)
    {
        var values = new HashSet<Guid>(ids.Length);
        foreach (var id in ids)
        {
            values.Add(id);
        }
        return CreateRange(values);
    }

    public static RoiIdSet CreateRange(IEnumerable<Guid> ids)
        => ids is RoiIdSet set ? set : new(ids.ToFrozenSet(), [], []);

    public static implicit operator RoiIdSet(ImmutableHashSet<Guid> ids) => CreateRange(ids);

    public bool Contains(Guid item) => _added.Contains(item) || (_items.Contains(item) && !_removed.Contains(item));

    public RoiIdSet Add(Guid item)
    {
        if (Contains(item))
        {
            return this;
        }

        if (_items.Contains(item))
        {
            return new(_items, _added, _removed.Remove(item));
        }

        return new(_items, _added.Add(item), _removed);
    }

    public RoiIdSet Remove(Guid item)
    {
        if (!Contains(item))
        {
            return this;
        }

        if (_items.Contains(item))
        {
            return new(_items, _added, _removed.Add(item));
        }

        return new(_items, _added.Remove(item), _removed);
    }

    public RoiIdSet Union(IEnumerable<Guid> other)
    {
        if (ReferenceEquals(this, other))
        {
            return this;
        }
        var edits = other.ToHashSet();
        if (edits.Count <= 64)
        {
            var result = this;
            foreach (var id in edits)
            {
                result = result.Add(id);
            }
            return result;
        }
        edits.UnionWith(this);
        return CreateRange(edits);
    }

    public RoiIdSet Except(IEnumerable<Guid> other)
    {
        if (ReferenceEquals(this, other))
        {
            return Empty;
        }
        var edits = other.ToHashSet();
        if (edits.Count <= 64)
        {
            var result = this;
            foreach (var id in edits)
            {
                result = result.Remove(id);
            }
            return result;
        }
        return CreateRange(this.Where(id => !edits.Contains(id)));
    }

    public bool SetEquals(IEnumerable<Guid> other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }
        var set = other as IReadOnlySet<Guid> ?? other.ToHashSet();
        return Count == set.Count && this.All(set.Contains);
    }

    public bool IsSubsetOf(IEnumerable<Guid> other)
    {
        var set = other as IReadOnlySet<Guid> ?? other.ToHashSet();
        return Count <= set.Count && this.All(set.Contains);
    }

    public bool IsProperSubsetOf(IEnumerable<Guid> other)
    {
        var set = other as IReadOnlySet<Guid> ?? other.ToHashSet();
        return Count < set.Count && this.All(set.Contains);
    }

    public bool IsSupersetOf(IEnumerable<Guid> other) => other.All(Contains);

    public bool IsProperSupersetOf(IEnumerable<Guid> other)
    {
        var set = other as IReadOnlySet<Guid> ?? other.ToHashSet();
        return Count > set.Count && set.All(Contains);
    }

    public bool Overlaps(IEnumerable<Guid> other) => other.Any(Contains);

    /// <summary>원소가 하나일 때 그 값을 반환한다. LINQ Single과 결과는 같고 반복자 할당이 없다.</summary>
    /// <remarks>선택이 비었거나 둘 이상이면 false를 반환하고 <paramref name="id"/>는 기본값이 된다.</remarks>
    public bool TryGetOnly(out Guid id)
    {
        if (Count != 1)
        {
            id = default;
            return false;
        }

        // Count가 1이므로 _items에서 지워지지 않은 첫 값 또는 _added의 첫 값 하나만 유효하다.
        foreach (var item in _items)
        {
            if (!_removed.Contains(item))
            {
                id = item;
                return true;
            }
        }

        foreach (var item in _added)
        {
            id = item;
            return true;
        }

        id = default;
        return false;
    }

    public IEnumerator<Guid> GetEnumerator()
    {
        foreach (var id in _items)
        {
            if (!_removed.Contains(id))
            {
                yield return id;
            }
        }
        foreach (var id in _added)
        {
            yield return id;
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
