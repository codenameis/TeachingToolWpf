using System.Collections.Immutable;

namespace VisionCanvas.Core.Editing;

/// <summary>Compressed batch deltas owned by the same document history entry as ordinary ROI deltas.</summary>
internal sealed class RepeatedRoiEdit
{
    private readonly ImmutableArray<(Guid Id, RepeatedRoiBatch? Before, RepeatedRoiBatch? After)> _changes;
    private readonly ImmutableDictionary<Guid, RoiCellSet> _beforeSelection;
    private readonly ImmutableDictionary<Guid, RoiCellSet> _afterSelection;
    public bool IsEmpty => _changes.IsEmpty;
    public long EstimatedBytes
    {
        get;
    }

    public RepeatedRoiEdit(DocumentState before, DocumentState after)
    {
        _beforeSelection = before.RepeatedSelection;
        _afterSelection = after.RepeatedSelection;
        _changes = ReferenceEquals(before.RepeatedRois, after.RepeatedRois) ? [] : before.RepeatedRois.Keys.Union(after.RepeatedRois.Keys)
            .Select(id => (Id: id, Before: before.RepeatedRois.GetValueOrDefault(id), After: after.RepeatedRois.GetValueOrDefault(id)))
            .Where(change => !ReferenceEquals(change.Before, change.After)).ToImmutableArray();
        EstimatedBytes = _changes.Sum(change => 64L + (change.Before?.EstimatedBytes ?? 0) + (change.After?.EstimatedBytes ?? 0))
            + _beforeSelection.Values.Sum(cells => 64L + (cells.Ranges.Length * 8L))
            + _afterSelection.Values.Sum(cells => 64L + (cells.Ranges.Length * 8L));
    }

    public DocumentState Apply(DocumentState state, bool forward)
    {
        var batches = state.RepeatedRois;
        foreach (var change in _changes)
        {
            var value = forward ? change.After : change.Before;
            batches = value is null ? batches.Remove(change.Id) : batches.SetItem(change.Id, value);
        }
        return RestoreSelection(ReferenceEquals(batches, state.RepeatedRois) ? state : state with
        {
            RepeatedRois = batches
        }, forward);
    }

    public DocumentState RestoreSelection(DocumentState state, bool forward)
    {
        var selected = forward ? _afterSelection : _beforeSelection;
        return ReferenceEquals(state.RepeatedSelection, selected) ? state : state with
        {
            RepeatedSelection = selected
        };
    }

    public bool Matches(DocumentState source, bool forward) => _changes.All(change =>
        ReferenceEquals(source.RepeatedRois.GetValueOrDefault(change.Id), forward ? change.Before : change.After));
}
