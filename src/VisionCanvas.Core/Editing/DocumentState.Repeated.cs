using System.Collections.Immutable;
using VisionGeometry;

namespace VisionCanvas.Core.Editing;

public sealed partial record DocumentState
{
    private ImmutableDictionary<Guid, RepeatedRoiBatch> _repeatedRois = ImmutableDictionary<Guid, RepeatedRoiBatch>.Empty;
    private ImmutableDictionary<Guid, RoiCellSet> _repeatedSelection = ImmutableDictionary<Guid, RoiCellSet>.Empty;
    public object RepeatedRowVersion { get; private set; } = new();

    public ImmutableDictionary<Guid, RepeatedRoiBatch> RepeatedRois
    {
        get => _repeatedRois;
        init
        {
            if (value.Count != _repeatedRois.Count || value.Any(pair => !_repeatedRois.TryGetValue(pair.Key, out var old)
                || old.Name != pair.Value.Name || old.Count != pair.Value.Count || old.Order != pair.Value.Order))
            {
                RepeatedRowVersion = new();
            }

            _repeatedRois = value;
            _transformSelection = null;
        }
    }

    public ImmutableDictionary<Guid, RoiCellSet> RepeatedSelection
    {
        get => _repeatedSelection;
        init
        {
            _repeatedSelection = value;
            _transformSelection = null;
        }
    }

    public long RoiCount => Rois.Count + RepeatedRois.Values.Sum(batch => (long)batch.Count);
    public long SelectionCount => Selection.Count + RepeatedSelection.Values.Sum(cells => (long)cells.Count);
    public RepeatedRoiCell? SingleRepeatedCell => SelectionCount == 1 && RepeatedSelection.Count == 1
        ? new(RepeatedSelection.First().Key, RepeatedSelection.First().Value.Ranges[0].Start) : null;

    public RepeatedRoiCell? HitRepeated(PointD point, double tolerance)
    {
        RepeatedRoiCell? hit = null;
        var order = long.MinValue;
        foreach (var batch in RepeatedRois.Values)
        {
            if (batch.Order >= order && batch.HitTest(point, tolerance) is { } index)
            {
                hit = new(batch.Id, index);
                order = batch.Order;
            }
        }
        return hit;
    }

    internal bool SameContent(DocumentState other) => ReferenceEquals(Rois, other.Rois) && ReferenceEquals(RepeatedRois, other.RepeatedRois);
    internal bool SameSelection(DocumentState other) => ReferenceEquals(Selection, other.Selection) && ReferenceEquals(RepeatedSelection, other.RepeatedSelection);
}
