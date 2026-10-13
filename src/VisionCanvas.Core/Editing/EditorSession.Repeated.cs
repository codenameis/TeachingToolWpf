using System.Collections.Immutable;
using VisionCanvas.Core.Geometry;
using VisionGeometry;

namespace VisionCanvas.Core.Editing;

public sealed partial class EditorSession
{
    public Guid AddRepeatedGrid(RepeatedRoiGrid grid)
    {
        if (State.Image is not { } image || !RoiImageBounds.Fits(grid.Frame.Bounds, image))
        {
            throw new ArgumentException("반복 ROI는 이미지 안에 있어야 합니다.", nameof(grid));
        }

        var batch = new RepeatedRoiBatch(Guid.NewGuid(), $"반복 ROI {_nextOrder + 1}", _nextOrder + 1, grid);
        Commit("반복 ROI 추가", State with
        {
            RepeatedRois = State.RepeatedRois.Add(batch.Id, batch),
            Selection = [],
            RepeatedSelection = ImmutableDictionary<Guid, RoiCellSet>.Empty.Add(batch.Id, batch.Cells)
        });
        _nextOrder++;
        return batch.Id;
    }

    public void SelectRepeated(RepeatedRoiCell cell, bool additive = false, bool toggle = false)
    {
        _ = State.RepeatedRois[cell.BatchId].Geometry(cell.Index);
        var selected = additive ? State.RepeatedSelection : ImmutableDictionary<Guid, RoiCellSet>.Empty;
        var existing = selected.GetValueOrDefault(cell.BatchId) ?? RoiCellSet.Empty;
        var single = RoiCellSet.Single(cell.Index);
        var cells = toggle && existing.Contains(cell.Index) ? existing.Except(single) : existing.Union(single);
        selected = cells.IsEmpty ? selected.Remove(cell.BatchId) : selected.SetItem(cell.BatchId, cells);
        Publish(State with
        {
            Selection = additive ? State.Selection : [],
            RepeatedSelection = selected
        });
    }

    public void SelectAll() => Publish(SelectAllState(State, CancellationToken.None));

    public void SelectEntries(IEnumerable<Guid> ids, bool preserveRepeatedRanges = false) => Publish(SelectEntriesState(State, ids, preserveRepeatedRanges, CancellationToken.None));

    public Task<PreparedEditorChange> PrepareEntrySelectionAsync(IEnumerable<Guid> ids, CancellationToken token = default, bool preserveRepeatedRanges = false)
        => PrepareChangeAsync(null, before => SelectEntriesState(before, ids, preserveRepeatedRanges, token), token);

    private static DocumentState SelectEntriesState(DocumentState before, IEnumerable<Guid> ids, bool preserveRepeatedRanges, CancellationToken token)
    {
        var ordinary = new HashSet<Guid>();
        var repeated = ImmutableDictionary.CreateBuilder<Guid, RoiCellSet>();
        foreach (var id in ids)
        {
            token.ThrowIfCancellationRequested();
            if (before.Rois.ContainsKey(id))
            {
                ordinary.Add(id);
            }
            else if (before.RepeatedRois.TryGetValue(id, out var batch))
            {
                repeated[id] = preserveRepeatedRanges && before.RepeatedSelection.TryGetValue(id, out var existing) ? existing : batch.Cells;
            }
        }
        return before with
        {
            Selection = RoiIdSet.CreateRange(ordinary),
            RepeatedSelection = repeated.ToImmutable()
        };
    }

    private static DocumentState SelectAllState(DocumentState before, CancellationToken token)
    {
        if (before.SelectionCount == before.RoiCount)
        {
            return before;
        }

        var ids = RoiIdSet.CreateRange(before.Rois.Keys.Select(id => { token.ThrowIfCancellationRequested(); return id; }));
        return before with
        {
            Selection = ids,
            RepeatedSelection = before.RepeatedRois.ToImmutableDictionary(pair => pair.Key, pair => pair.Value.Cells)
        };
    }

    public void SelectRegion(BoundsD bounds, bool additive = false) => Publish(SelectRegionState(State, bounds, additive, CancellationToken.None));

    public Task<PreparedEditorChange> PrepareRegionSelectionAsync(BoundsD bounds, bool additive = false, CancellationToken token = default)
        => PrepareChangeAsync(null, before => SelectRegionState(before, bounds, additive, token), token);

    private static DocumentState SelectRegionState(DocumentState before, BoundsD bounds, bool additive, CancellationToken token)
    {
        var ids = before.Snapshot.Spatial.Intersecting(bounds).Where(roi =>
        {
            token.ThrowIfCancellationRequested();
            var box = roi.Geometry.Bounds;
            return bounds.Contains(new(box.Left, box.Top)) && bounds.Contains(new(box.Right, box.Bottom));
        }).Select(roi => roi.Id);
        var selected = additive ? before.Selection.Union(ids) : RoiIdSet.CreateRange(ids);
        var repeated = additive ? before.RepeatedSelection : ImmutableDictionary<Guid, RoiCellSet>.Empty;
        foreach (var (id, batch) in before.RepeatedRois)
        {
            var cells = batch.Contained(bounds, token);
            if (repeated.TryGetValue(id, out var previous))
            {
                cells = cells.Union(previous);
            }

            if (!cells.IsEmpty)
            {
                repeated = repeated.SetItem(id, cells);
            }
        }
        return before with
        {
            Selection = selected,
            RepeatedSelection = repeated
        };
    }

    public void SetRepeatedGeometry(RepeatedRoiCell cell, RoiGeometry geometry)
        => Commit("ROI 속성 변경", WithRepeatedGeometry(State, cell, geometry));

    public void PreviewRepeatedGeometry(RepeatedRoiCell cell, RoiGeometry geometry)
        => Publish(WithRepeatedGeometry(RequirePreviewStart(), cell, geometry));

    private static DocumentState WithRepeatedGeometry(DocumentState before, RepeatedRoiCell cell, RoiGeometry geometry)
    {
        if (before.Image is { } image && !RoiImageBounds.Fits(geometry.Bounds, image))
        {
            throw new ArgumentException("ROI는 이미지 안에 있어야 합니다.", nameof(geometry));
        }

        var batch = before.RepeatedRois[cell.BatchId];
        var next = batch.WithGeometry(cell.Index, geometry);
        return ReferenceEquals(batch, next) ? before : before with
        {
            RepeatedRois = before.RepeatedRois.SetItem(cell.BatchId, next)
        };
    }

    private static DocumentState DeleteRepeatedSelection(DocumentState before)
    {
        var batches = before.RepeatedRois;
        foreach (var (id, selected) in before.RepeatedSelection)
        {
            var batch = batches[id];
            var next = batch.Slice(batch.Cells.Except(selected));
            batches = next is null ? batches.Remove(id) : batches.SetItem(id, next);
        }
        return before with
        {
            RepeatedRois = batches,
            RepeatedSelection = ImmutableDictionary<Guid, RoiCellSet>.Empty
        };
    }
}
