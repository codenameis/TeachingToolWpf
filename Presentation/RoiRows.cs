using System.Collections;
using System.Collections.Immutable;
using TeachingToolWpf.Core.Editing;

namespace TeachingToolWpf.Presentation;

/// <summary>UI-owned row index. Consumers have a read-only IList; displayed pages are independent copies.</summary>
public sealed class RoiRows : IList, IReadOnlyList<RoiRow>
{
    private readonly RoiRow[] items;
    private readonly Dictionary<Guid, int> indices;
    public RoiRows(IEnumerable<RoiRow> rows)
    {
        items = rows.ToArray();
        indices = items.Select((row, index) => (row.Id, index)).ToDictionary(pair => pair.Id, pair => pair.index);
    }
    public RoiRow? Find(Guid id) => indices.TryGetValue(id, out var index) ? items[index] : null;
    public int IndexOf(Guid id) => indices.GetValueOrDefault(id, -1);
    public RoiRows Page(int page, int size) => new(items.Skip(page * size).Take(size));
    internal bool TryRefreshMetadata(ImmutableDictionary<Guid, Roi>? previous, DocumentState state)
    {
        if (!ReferenceEquals(previous, state.Snapshot.PreviousRois) || previous is null) return false;
        foreach (var id in state.Snapshot.ChangedIds)
        {
            var old = previous.GetValueOrDefault(id); var next = state.Rois.GetValueOrDefault(id);
            if (old is null || next is null || old.ParentId != next.ParentId || old.Order != next.Order) return false;
        }
        foreach (var id in state.Snapshot.ChangedIds)
        {
            var roi = state.Rois[id]; var index = indices[id];
            var count = state.Snapshot.Children.GetValueOrDefault(id, []).Length;
            items[index] = items[index] with { Label = roi.Name, Kind = RoiRow.Describe(roi, count) };
        }
        return true;
    }
    public RoiRow this[int index] => items[index];
    object? IList.this[int index] { get => items[index]; set => throw new NotSupportedException(); }
    public int Count => items.Length;
    public bool IsReadOnly => true;
    public bool IsFixedSize => true;
    public bool IsSynchronized => false;
    public object SyncRoot => this;
    public int IndexOf(object? value) => value is RoiRow row && indices.TryGetValue(row.Id, out var index) && items[index] == row ? index : -1;
    public bool Contains(object? value) => IndexOf(value) >= 0;
    public void CopyTo(Array array, int index) => items.CopyTo(array, index);
    public IEnumerator<RoiRow> GetEnumerator() => ((IEnumerable<RoiRow>)items).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => items.GetEnumerator();
    public int Add(object? value) => throw new NotSupportedException();
    public void Insert(int index, object? value) => throw new NotSupportedException();
    public void Remove(object? value) => throw new NotSupportedException();
    public void RemoveAt(int index) => throw new NotSupportedException();
    public void Clear() => throw new NotSupportedException();
}
