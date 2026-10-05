using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using VisionCanvas.Core.Editing;
using VisionGeometry;

namespace TeachingToolWpf.Core.Tests;

public class HistorySnapshotCacheTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FirstUndoAndRedoRetainCommittedSnapshotsAndLatestViewport(bool preparedCommit)
    {
        var editor = new EditorSession();
        editor.InitializeImage(new("image", "image", 1000, 1000), ViewportTransform.Identity);
        editor.AddRange(Enumerable.Range(0, 384).Select(index => new RoiGeometry(
            new(20 + (index % 24 * 30), 20 + (index / 24 * 30)), 10, 10, index % 90)));
        editor.Select(editor.State.Rois.Keys);
        var before = editor.State;
        var beforeSpatial = before.Snapshot.Spatial;
        var beforeSelected = before.SelectedSpatial;
        if (preparedCommit)
        {
            editor.CommitPrepared(await editor.PrepareTranslationAsync(new(2, 3)));
        }
        else
        {
            editor.TranslateSelection(new(2, 3));
        }
        var after = editor.State;
        var afterSpatial = after.Snapshot.Spatial;
        var afterSelected = after.SelectedSpatial;

        var undo = await editor.PrepareHistoryAsync(false);
        Assert.Same(before.Snapshot, undo.State.Snapshot);
        var view = after.Viewport.Pan(new(40, 50));
        editor.SetViewport(view);
        editor.CommitPreparedHistory(undo);
        Assert.Same(before.Rois, editor.State.Rois);
        Assert.Same(before.Selection, editor.State.Selection);
        Assert.Same(beforeSpatial, editor.State.Snapshot.Spatial);
        Assert.Same(beforeSelected, editor.State.SelectedSpatial);
        Assert.Equal(view, editor.State.Viewport);

        var redo = await editor.PrepareHistoryAsync(true);
        Assert.Same(after.Snapshot, redo.State.Snapshot);
        view = view.Pan(new(10, -20));
        editor.SetViewport(view);
        editor.CommitPreparedHistory(redo);
        Assert.Same(after.Rois, editor.State.Rois);
        Assert.Same(after.Selection, editor.State.Selection);
        Assert.Same(afterSpatial, editor.State.Snapshot.Spatial);
        Assert.Same(afterSelected, editor.State.SelectedSpatial);
        Assert.Equal(view, editor.State.Viewport);
    }

    [Fact]
    public async Task CachedPreparationStillHonorsCancellationAndStaleSelection()
    {
        var editor = new EditorSession();
        editor.Add(new(new(100, 100), 10, 10, 0));
        var before = editor.State;
        editor.TranslateSelection(new(5, 0));
        var after = editor.State;
        var bytes = editor.HistoryBytes;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => editor.PrepareHistoryAsync(false, cancellation.Token));
        Assert.Same(after, editor.State);
        Assert.Equal(bytes, editor.HistoryBytes);
        Assert.True(editor.CanUndo);
        Assert.False(editor.CanRedo);

        var prepared = await editor.PrepareHistoryAsync(false);
        Assert.Same(before.Snapshot, prepared.State.Snapshot);
        editor.Select([]);
        var changed = editor.State;
        Assert.Throws<InvalidOperationException>(() => editor.CommitPreparedHistory(prepared));
        Assert.Same(changed, editor.State);
        Assert.Equal(bytes, editor.HistoryBytes);
        Assert.False(editor.CanRedo);
    }

    [Theory]
    [InlineData("rois")]
    [InlineData("selection")]
    [InlineData("image")]
    public void CachedPairRequiresExactSourceMapSelectionAndImage(string changedPart)
    {
        var (history, before, after) = CreateHistory();
        var undone = history.Undo(after);
        var changed = changedPart switch
        {
            "rois" => undone with { Rois = undone.Rois.ToDictionary(pair => pair.Key, pair => pair.Value).ToImmutableDictionary() },
            "selection" => undone with { Selection = undone.Selection.ToArray().ToImmutableHashSet() },
            _ => undone with { Image = new("different", "different", 1000, 1000) }
        };
        if (changedPart == "rois")
        {
            Assert.NotSame(undone.Rois, changed.Rois);
        }
        if (changedPart == "selection")
        {
            Assert.NotSame(undone.Selection, changed.Selection);
        }

        var prepared = history.CaptureRedo()!.Prepare(changed);
        Assert.NotSame(after.Snapshot, prepared.Target.Snapshot);
        Assert.Equal(changed.Image, prepared.Target.Image);
        Assert.Equal(after.Rois.OrderBy(pair => pair.Key), prepared.Target.Rois.OrderBy(pair => pair.Key));
        Assert.True(prepared.Target.Selection.SetEquals(after.Selection));
        Assert.Same(before.Image, undone.Image);
    }

    [Fact]
    public void ImageHistoryRestoresRecordedViewsAcrossCachedOppositeDirections()
    {
        var editor = new EditorSession();
        editor.InitializeImage(new("before", "before", 1000, 1000), new(2, new(10, 20)));
        editor.Add(new(new(100, 100), 10, 10, 0));
        var before = editor.State;
        editor.SetImage(new("after", "after", 2000, 2000), new(3, new(30, 40)));
        var after = editor.State;
        editor.SetViewport(after.Viewport.Pan(new(100, 100)));
        editor.Undo();
        Assert.Same(before.Snapshot, editor.State.Snapshot);
        Assert.Equal(before.Viewport, editor.State.Viewport);
        Assert.Equal(before.Image, editor.State.Image);
        editor.SetViewport(before.Viewport.Pan(new(-50, -60)));
        editor.Redo();
        Assert.Same(after.Snapshot, editor.State.Snapshot);
        Assert.Equal(after.Viewport, editor.State.Viewport);
        Assert.Equal(after.Image, editor.State.Image);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SelectionChangesBeforeUndoAndRedoStillRestoreRecordedSelections(bool capturedPreparation)
    {
        var (history, before, after) = CreateHistory();
        var changedAfter = after with
        {
            Selection = before.Selection
        };
        _ = changedAfter.SelectedSpatial;
        var undone = Move(changedAfter, redo: false);
        Assert.Same(before.Selection, undone.Selection);
        var redone = Move(undone, redo: true);
        Assert.Same(after.Snapshot, redone.Snapshot);
        Assert.Same(after.Selection, redone.Selection);
        Assert.Equal(after.Selection.Single(), Assert.Single(redone.SelectedSpatial.Intersecting(redone.Snapshot.Spatial.Bounds)).Id);

        undone = Move(redone, redo: false);
        var changedBefore = undone with
        {
            Selection = ImmutableHashSet<Guid>.Empty
        };
        _ = changedBefore.SelectedSpatial;
        redone = Move(changedBefore, redo: true);
        Assert.Same(after.Selection, redone.Selection);
        undone = Move(redone, redo: false);
        Assert.Same(changedBefore.Snapshot, undone.Snapshot);
        Assert.Same(before.Selection, undone.Selection);
        Assert.Equal(before.Selection.Single(), Assert.Single(undone.SelectedSpatial.Intersecting(undone.Snapshot.Spatial.Bounds)).Id);

        DocumentState Move(DocumentState source, bool redo)
            => capturedPreparation
                ? history.Commit((redo ? history.CaptureRedo() : history.CaptureUndo())!.Prepare(source), source)
                : redo ? history.Redo(source) : history.Undo(source);
    }

    [Theory]
    [InlineData("name", false)]
    [InlineData("name", true)]
    [InlineData("geometry", false)]
    [InlineData("geometry", true)]
    [InlineData("remove", false)]
    [InlineData("remove", true)]
    [InlineData("add", false)]
    [InlineData("add", true)]
    public void MismatchedSourceValuesRestoreRecordedEditInBothDirections(string mismatch, bool capturedPreparation)
    {
        var a = new Roi(Guid.NewGuid(), "A", new(new(100, 100), 10, 10, 0), null, false, 1);
        var b = mismatch == "geometry" ? a with
        {
            Geometry = a.Geometry.Translate(new(20, 0))
        } : a with
        {
            Name = "B"
        };
        var c = a with
        {
            Name = "C",
            Geometry = a.Geometry.Translate(new(40, 0))
        };
        var unrelated = new Roi(Guid.NewGuid(), "unrelated", new(new(300, 100), 10, 10, 0), null, false, 2);
        var changedUnrelated = unrelated with
        {
            Name = "preserved outside edit"
        };
        var rois = ImmutableDictionary<Guid, Roi>.Empty.Add(unrelated.Id, unrelated);
        var before = DocumentState.Empty with
        {
            Rois = mismatch == "add" ? rois : rois.Add(a.Id, a)
        };
        var after = before.WithRois(mismatch == "remove" ? before.Rois.Remove(a.Id) : before.Rois.SetItem(a.Id, b), [a.Id]);
        var history = new EditHistory();
        history.Push(new("recorded A to B", before, after));
        var expectedBefore = before.Rois.SetItem(unrelated.Id, changedUnrelated);
        var expectedAfter = after.Rois.SetItem(unrelated.Id, changedUnrelated);

        var changedAfter = after with
        {
            Rois = (mismatch == "add" ? expectedAfter.Remove(a.Id) : expectedAfter.SetItem(a.Id, c))
        };
        var undone = Move(changedAfter, redo: false);
        Assert.Equal(expectedBefore.OrderBy(pair => pair.Key), undone.Rois.OrderBy(pair => pair.Key));
        var redone = Move(undone, redo: true);
        Assert.Equal(expectedAfter.OrderBy(pair => pair.Key), redone.Rois.OrderBy(pair => pair.Key));

        undone = Move(redone, redo: false);
        var changedBefore = undone with
        {
            Rois = (mismatch == "remove" ? expectedBefore.Remove(a.Id) : expectedBefore.SetItem(a.Id, c))
        };
        redone = Move(changedBefore, redo: true);
        Assert.Equal(expectedAfter.OrderBy(pair => pair.Key), redone.Rois.OrderBy(pair => pair.Key));
        undone = Move(redone, redo: false);
        Assert.Equal(expectedBefore.OrderBy(pair => pair.Key), undone.Rois.OrderBy(pair => pair.Key));

        DocumentState Move(DocumentState source, bool redo)
            => capturedPreparation
                ? history.Commit((redo ? history.CaptureRedo() : history.CaptureUndo())!.Prepare(source), source)
                : redo ? history.Redo(source) : history.Undo(source);
    }

    [Fact]
    public void OversizePushPreservesRecentPairAndRedoIdentity()
    {
        var (history, _, after) = CreateHistory(byteBudget: 1000);
        var undone = history.Undo(after);
        var bytes = history.Bytes;
        var value = after.Rois.Values.OrderBy(roi => roi.Order).Last() with
        {
            Name = new string('X', 2000)
        };
        var rejected = undone.WithRois(undone.Rois.Add(value.Id, value), [value.Id]);
        Assert.Throws<InvalidOperationException>(() => history.Push(new("oversize", undone, rejected)));
        Assert.Equal(bytes, history.Bytes);
        Assert.True(history.CanRedo);
        var redone = history.Redo(undone);
        Assert.Same(after.Snapshot, redone.Snapshot);
        Assert.Same(after.Rois, redone.Rois);
    }

    [Fact]
    public void SuccessfulBranchDropsThePriorSnapshotPair()
    {
        var (history, obsolete) = ReplaceCachedPair();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(obsolete.TryGetTarget(out _));
        Assert.False(history.CanRedo);
        Assert.Equal("branch", history.UndoName);
        GC.KeepAlive(history);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (EditHistory History, WeakReference<RoiSnapshot> Obsolete) ReplaceCachedPair()
    {
        var (history, _, after) = CreateHistory();
        var undone = history.Undo(after);
        var obsolete = new WeakReference<RoiSnapshot>(after.Snapshot);
        var added = new Roi(Guid.NewGuid(), "branch", new(new(250, 100), 10, 10, 0), null, false, 3);
        var branch = undone.WithRois(undone.Rois.Add(added.Id, added), [added.Id]);
        history.Push(new("branch", undone, branch));
        return (history, obsolete);
    }

    private static (EditHistory History, DocumentState Before, DocumentState After) CreateHistory(long byteBudget = 1024 * 1024)
    {
        var first = new Roi(Guid.NewGuid(), "first", new(new(100, 100), 10, 10, 0), null, false, 1);
        var added = new Roi(Guid.NewGuid(), "added", new(new(150, 100), 10, 10, 0), null, false, 2);
        var before = DocumentState.Empty with
        {
            Rois = ImmutableDictionary<Guid, Roi>.Empty.Add(first.Id, first),
            Selection = [first.Id],
            Image = new("image", "image", 1000, 1000)
        };
        var after = before.WithRois(before.Rois.Add(added.Id, added), [added.Id]) with
        {
            Selection = [added.Id]
        };
        var history = new EditHistory(byteBudget: byteBudget);
        history.Push(new("add", before, after));
        return (history, before, after);
    }
}
