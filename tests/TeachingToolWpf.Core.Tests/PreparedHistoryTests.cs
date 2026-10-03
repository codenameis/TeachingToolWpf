using System.Collections.Immutable;
using TeachingToolWpf.Core.Editing;
using TeachingToolWpf.Core.Geometry;

namespace TeachingToolWpf.Core.Tests;

public class PreparedHistoryTests
{
    [Fact]
    public void RepeatedPreparationAndCommitShareOneImmutableTarget()
    {
        var history = new EditHistory();
        var before = DocumentState.Empty;
        var roi = CreateRoi();
        var after = before.WithRois(before.Rois.Add(roi.Id, roi), [roi.Id]) with
        {
            Selection = [roi.Id]
        };
        history.Push(new("add", before, after));

        var prepared = history.PreviewUndo(after);
        Assert.True(history.CanUndo);
        Assert.Same(prepared, history.PreviewUndo(after));
        Assert.Same(prepared, history.Undo(after));
        Assert.Empty(prepared.Rois);
        Assert.Empty(prepared.Selection);
        Assert.False(history.CanUndo);
        Assert.True(history.CanRedo);
        var redo = history.PreviewRedo(prepared);
        Assert.Same(redo, history.Redo(prepared));
        Assert.Equal(roi, redo.Rois[roi.Id]);
        Assert.Contains(roi.Id, redo.Selection);
    }

    [Fact]
    public void ChangedSourceStateCannotReuseAStalePreparedViewOrImage()
    {
        var history = new EditHistory();
        var before = DocumentState.Empty;
        var roi = CreateRoi();
        var after = before.WithRois(before.Rois.Add(roi.Id, roi), [roi.Id]);
        history.Push(new("add", before, after));
        var oldTarget = history.PreviewUndo(after);
        var changed = after with
        {
            Viewport = after.Viewport.Pan(new(25, 40)),
            Image = new("current", "current", 500, 500)
        };
        var target = history.PreviewUndo(changed);
        Assert.NotSame(oldTarget, target);
        Assert.Equal(changed.Viewport, target.Viewport);
        Assert.Equal(changed.Image, target.Image);
        Assert.Same(target, history.Undo(changed));
    }

    [Fact]
    public void OppositeDirectionsAndChangedCursorPrepareTheirOwnTargets()
    {
        var history = new EditHistory();
        var empty = DocumentState.Empty;
        var a = CreateRoi();
        var b = CreateRoi();
        var first = empty.WithRois(empty.Rois.Add(a.Id, a), [a.Id]);
        var second = first.WithRois(first.Rois.Add(b.Id, b), [b.Id]);
        history.Push(new("A", empty, first));
        history.Push(new("B", first, second));
        var current = history.Undo(second);
        var undo = history.PreviewUndo(current);
        var redo = history.PreviewRedo(current);
        Assert.Empty(undo.Rois);
        Assert.Equal(2, redo.Rois.Count);
        Assert.NotSame(undo, redo);
        var emptyAgain = history.Undo(current);
        Assert.Empty(emptyAgain.Rois);
        Assert.False(history.CanUndo);
        var restoreA = history.PreviewRedo(emptyAgain);
        Assert.Equal(a.Id, Assert.Single(restoreA.Rois).Key);
    }

    [Fact]
    public void FailedOversizePushPreservesPreparedRedoAndBudget()
    {
        var history = new EditHistory(byteBudget: 1000);
        var before = DocumentState.Empty;
        var roi = CreateRoi();
        var after = before.WithRois(before.Rois.Add(roi.Id, roi), [roi.Id]);
        history.Push(new("add", before, after));
        var current = history.Undo(after);
        var target = history.PreviewRedo(current);
        var bytes = history.Bytes;
        var huge = roi with
        {
            Name = new string('X', 2000)
        };
        var rejected = current.WithRois(current.Rois.Add(huge.Id, huge), [huge.Id]);
        Assert.Throws<InvalidOperationException>(() => history.Push(new("too large", current, rejected)));
        Assert.Equal(bytes, history.Bytes);
        Assert.True(history.CanRedo);
        Assert.False(history.CanUndo);
        Assert.Same(target, history.PreviewRedo(current));
        Assert.Same(target, history.Redo(current));
    }

    [Fact]
    public void SuccessfulBranchReplacesPreparedRedoAndKeepsAtomicBudgetEviction()
    {
        var history = new EditHistory(maxCount: 1);
        var empty = DocumentState.Empty;
        var a = CreateRoi();
        var b = CreateRoi();
        var first = empty.WithRois(empty.Rois.Add(a.Id, a), [a.Id]);
        history.Push(new("A", empty, first));
        var current = history.Undo(first);
        _ = history.PreviewRedo(current);
        var branch = current.WithRois(current.Rois.Add(b.Id, b), [b.Id]);
        var edit = new DocumentEdit("B", current, branch);
        history.Push(edit);
        Assert.False(history.CanRedo);
        Assert.Equal(edit.EstimatedBytes, history.Bytes);
        var undone = history.Undo(branch);
        Assert.Empty(undone.Rois);
        Assert.False(history.CanUndo);
        Assert.Equal(b.Id, Assert.Single(history.Redo(undone).Rois).Key);
    }

    [Fact]
    public void CancelledEditCaptureDoesNotReplaceRedoAndSameMapImageEditStillExists()
    {
        var history = new EditHistory();
        var before = DocumentState.Empty;
        var roi = CreateRoi();
        var after = before.WithRois(before.Rois.Add(roi.Id, roi), [roi.Id]);
        history.Push(new("add", before, after));
        var current = history.Undo(after);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => new DocumentEdit("cancelled", current, after, cancellationToken: cancellation.Token));
        Assert.True(history.CanRedo);
        Assert.False(history.CanUndo);
        var imageEdit = new DocumentEdit("same image", after, after, replacesImage: true);
        Assert.Empty(imageEdit.Changes);
        Assert.False(imageEdit.IsEmpty);
        Assert.True(new DocumentEdit("no change", after, after).IsEmpty);
    }

    private static Roi CreateRoi() => new(Guid.NewGuid(), "ROI", new(new(50, 50), 10, 10, 0), null, false, 1);
}
