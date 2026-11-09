using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using VisionCanvas.Core.Editing;

namespace TeachingToolWpf.Models;

/// <summary>UI-owned row index. Consumers have a read-only IList; displayed pages are independent copies.</summary>
public sealed class RoiRows : IList, IReadOnlyList<RoiRow>
{
    private readonly RoiRow[] _items;
    private readonly Dictionary<Guid, int> _indices;

    public RoiRows(IEnumerable<RoiRow> rows)
    {
        _items = rows.ToArray();
        _indices = _items.Select((row, index) => (row.Id, index)).ToDictionary(pair => pair.Id, pair => pair.index);
    }

    public RoiRow? Find(Guid id) => _indices.TryGetValue(id, out var index) ? _items[index] : null;

    public int IndexOf(Guid id) => _indices.GetValueOrDefault(id, -1);

    public RoiRows Page(int page, int size) => new(_items.Skip(page * size).Take(size));

    internal void ApplyAssignments(IReadOnlyDictionary<Guid, RoiAlgorithmAssignment> assignments)
    {
        for (var i = 0; i < _items.Length; i++)
        {
            var current = _items[i];
            if (assignments.TryGetValue(current.Id, out var assignment))
            {
                _items[i] = current with
                {
                    AlgorithmBadge = assignment.AlgorithmBadge,
                    AlgorithmAccent = assignment.AlgorithmAccent,
                    VerdictBadge = assignment.VerdictBadge,
                    VerdictAccent = assignment.VerdictAccent,
                    PatternThumbnail = assignment.PatternThumbnail
                };
            }
            else if (current.HasAlgorithm || current.HasVerdict || current.HasPatternThumbnail)
            {
                _items[i] = current with
                {
                    AlgorithmBadge = "미지정",
                    AlgorithmAccent = "#64748B",
                    VerdictBadge = "",
                    VerdictAccent = "#64748B",
                    PatternThumbnail = null
                };
            }
        }
    }

    internal bool TryRefreshMetadata(
        ImmutableDictionary<Guid, Roi>? previous,
        DocumentState state,
        IReadOnlyDictionary<Guid, RoiAlgorithmAssignment>? assignments = null)
    {
        if (!ReferenceEquals(previous, state.Snapshot.PreviousRois) || previous is null)
        {
            return false;
        }

        foreach (var id in state.Snapshot.ChangedIds)
        {
            var old = previous.GetValueOrDefault(id);
            var next = state.Rois.GetValueOrDefault(id);
            if (old is null || next is null || old.ParentId != next.ParentId || old.Order != next.Order)
            {
                return false;
            }
        }

        foreach (var id in state.Snapshot.ChangedIds)
        {
            var roi = state.Rois[id];
            var index = _indices[id];
            var count = state.Snapshot.Children.GetValueOrDefault(id, []).Length;
            var updated = _items[index] with
            {
                Label = roi.Name,
                Kind = RoiRow.Describe(roi, count, state.Snapshot.GroupLevels.GetValueOrDefault(id)),
                ShapeGlyph = RoiRow.ResolveShapeGlyph(roi, count)
            };

            if (assignments is not null && assignments.TryGetValue(id, out var assignment))
            {
                updated = updated with
                {
                    AlgorithmBadge = assignment.AlgorithmBadge,
                    AlgorithmAccent = assignment.AlgorithmAccent,
                    VerdictBadge = assignment.VerdictBadge,
                    VerdictAccent = assignment.VerdictAccent,
                    PatternThumbnail = assignment.PatternThumbnail
                };
            }

            _items[index] = updated;
        }

        return true;
    }

    public RoiRow this[int index] => _items[index];

    object? IList.this[int index]
    {
        get => _items[index];
        set => throw new NotSupportedException();
    }

    public int Count => _items.Length;

    public bool IsReadOnly => true;

    public bool IsFixedSize => true;

    public bool IsSynchronized => false;

    public object SyncRoot => this;

    public int IndexOf(object? value) => value is RoiRow row && _indices.TryGetValue(row.Id, out var index) && _items[index] == row ? index : -1;

    public bool Contains(object? value) => IndexOf(value) >= 0;

    public void CopyTo(Array array, int index) => _items.CopyTo(array, index);

    public IEnumerator<RoiRow> GetEnumerator() => ((IEnumerable<RoiRow>)_items).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();

    public int Add(object? value) => throw new NotSupportedException();

    public void Insert(int index, object? value) => throw new NotSupportedException();

    public void Remove(object? value) => throw new NotSupportedException();

    public void RemoveAt(int index) => throw new NotSupportedException();

    public void Clear() => throw new NotSupportedException();
}
