using System.Collections.Immutable;
using TeachingToolWpf.Core.Editing;
using TeachingToolWpf.Core.Geometry;

namespace TeachingToolWpf.Core.Tests;

public class PreparedHistoryStepTests
{
    [Fact]
    public async Task WorkerPreparationPreservesCurrentPanWhenOwnerCommitsOnce()
    {
        var (history, source) = CreateHistory();
        var step = Assert.IsType<HistoryStep>(history.CaptureUndo());
        var target = await Task.Run(() => step.Prepare(source));
        Assert.True(history.CanUndo);
        Assert.False(history.CanRedo);
        var current = source with
        {
            Viewport = source.Viewport.Pan(new(40, 70))
        };
        var result = history.Commit(target, current);
        Assert.Empty(result.Rois);
        Assert.Equal(current.Viewport, result.Viewport);
        Assert.False(history.CanUndo);
        Assert.True(history.CanRedo);
        Assert.Throws<InvalidOperationException>(() => history.Commit(target, current));
        Assert.False(history.CanUndo);
        Assert.True(history.CanRedo);
    }

    [Fact]
    public void CancelledPreparationLeavesCursorAndRedoUntouched()
    {
        var (history, source) = CreateHistory();
        var step = Assert.IsType<HistoryStep>(history.CaptureUndo());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => step.Prepare(source, cancellation.Token));
        Assert.True(history.CanUndo);
        Assert.False(history.CanRedo);
        Assert.Empty(history.Commit(step.Prepare(source), source).Rois);
    }

    [Fact]
    public void ChangedDocumentRejectsPreparedResultWithoutMovingCursor()
    {
        var (history, source) = CreateHistory();
        var target = history.CaptureUndo()!.Prepare(source);
        var roi = source.Rois.Values.Single();
        var changedGeometry = source.WithRois(source.Rois.SetItem(roi.Id, roi with
        {
            Geometry = roi.Geometry.Translate(new(1, 0))
        }), [roi.Id]);
        var changedSelection = source with
        {
            Selection = ImmutableHashSet<Guid>.Empty
        };
        var changedImage = source with
        {
            Image = new("different", "different", 100, 100)
        };
        foreach (var changed in new[] { changedGeometry, changedSelection, changedImage })
        {
            Assert.Throws<InvalidOperationException>(() => history.Commit(target, changed));
            Assert.True(history.CanUndo);
            Assert.False(history.CanRedo);
        }
        Assert.Empty(history.Commit(target, source).Rois);
    }

    [Fact]
    public void HistoryRevisionAndOwnerIdentityPreventStaleCommit()
    {
        var (history, source) = CreateHistory();
        var target = history.CaptureUndo()!.Prepare(source);
        var undone = history.Undo(source);
        _ = history.Redo(undone);
        Assert.Throws<InvalidOperationException>(() => history.Commit(target, source));
        var other = new EditHistory();
        other.Push(new("same data", DocumentState.Empty, source));
        Assert.Throws<InvalidOperationException>(() => other.Commit(history.CaptureUndo()!.Prepare(source), source));
        Assert.True(history.CanUndo);
        Assert.True(other.CanUndo);
    }

    [Fact]
    public void PreparedImageUndoRestoresRecordedViewDespitePanDuringPreparation()
    {
        var history = new EditHistory();
        var before = DocumentState.Empty with
        {
            Image = new("before", "before", 100, 100),
            Viewport = ViewportTransform.Identity.Pan(new(10, 20))
        };
        var after = before with
        {
            Image = new("after", "after", 500, 500),
            Viewport = ViewportTransform.Identity
        };
        history.Push(new("image", before, after, replacesImage: true));
        var target = history.CaptureUndo()!.Prepare(after);
        var result = history.Commit(target, after with
        {
            Viewport = after.Viewport.Pan(new(80, 90))
        });
        Assert.Equal(before.Image, result.Image);
        Assert.Equal(before.Viewport, result.Viewport);
    }

    [Fact]
    public void IdenticalEmptyImageHistoryRejectsStaleTargetBeforeSourceTransfer()
    {
        var history = new EditHistory();
        var state = DocumentState.Empty with
        {
            Image = new("same", "same", 100, 100)
        };
        history.Push(new("reopen same image", state, state, replacesImage: true));
        var stale = history.CaptureUndo()!.Prepare(state);
        history.Validate(stale, state);
        var undone = history.Undo(state);
        var restored = history.Redo(undone);
        Assert.Equal(state, restored);
        Assert.Throws<InvalidOperationException>(() => history.Validate(stale, state));
        Assert.True(history.CanUndo);
        Assert.False(history.CanRedo);
        var current = history.CaptureUndo()!.Prepare(state);
        history.Validate(current, state);
        Assert.Equal(state, history.Commit(current, state));
        Assert.False(history.CanUndo);
        Assert.True(history.CanRedo);
    }

    private static (EditHistory History, DocumentState Source) CreateHistory()
    {
        var history = new EditHistory();
        var empty = DocumentState.Empty;
        var roi = new Roi(Guid.NewGuid(), "roi", new(new(50, 50), 10, 10, 0), null, false, 1);
        var source = empty.WithRois(empty.Rois.Add(roi.Id, roi), [roi.Id]) with
        {
            Selection = [roi.Id]
        };
        history.Push(new("add", empty, source));
        return (history, source);
    }
}
