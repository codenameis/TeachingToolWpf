using System.Collections.Immutable;
using TeachingToolWpf.Core.Editing;
using TeachingToolWpf.Core.Geometry;
using TeachingToolWpf.Core.Interaction;

namespace TeachingToolWpf.Core.Tests;

public class HistoryAndHierarchyTests
{
    [Fact] public void FailedGroupPreservesDocumentAndRedo()
    {
        var editor = new EditorSession();
        editor.Add(new(new(0, 0), 60, 40, 0));
        var a = editor.Add(new(new(0, 0), 20, 20, 0)); var b = editor.Add(new(new(100, 0), 20, 20, 0));
        editor.Add(new(new(500, 500), 20, 20, 0)); editor.Undo();
        editor.Select([a, b]); var before = editor.State;
        Assert.Throws<InvalidOperationException>(() => editor.GroupSelection());
        Assert.Same(before, editor.State); Assert.True(editor.CanRedo);
    }
    [Fact] public void ParentResizePreservesChildSizeAndRelativePositionAndReparentsOnCommit()
    {
        var editor = new EditorSession(); var parent = editor.Add(new(new(100, 100), 100, 100, 0));
        var child = editor.Add(new(new(75, 75), 20, 20, 0)); var shape = editor.State.Rois[child].Geometry;
        editor.Select([parent]); editor.BeginPreview();
        editor.PreviewResize(parent, new(new(125, 125), 50, 50, 0));
        Assert.Equal(shape.Translate(new(25, 25)), editor.State.Rois[child].Geometry); Assert.Equal(parent, editor.State.Rois[child].ParentId);
        editor.CommitPreview("resize"); Assert.Null(editor.State.Rois[child].ParentId);
        editor.Undo(); Assert.Equal(parent, editor.State.Rois[child].ParentId); Assert.Equal(shape, editor.State.Rois[child].Geometry);
    }
    [Fact] public void AltClickCyclesCoincidentRois()
    {
        var editor = new EditorSession(); var a = editor.Add(new(new(100, 100), 100, 100, 0)); var b = editor.Add(new(new(100, 100), 100, 100, 0));
        var input = new InteractionController(editor);
        input.Down(new(100, 100), InputModifiers.Alt); input.Up(new(100, 100), InputModifiers.Alt);
        Assert.Contains(a, editor.State.Selection);
        input.Down(new(100, 100), InputModifiers.Alt); input.Up(new(100, 100), InputModifiers.Alt);
        Assert.Contains(b, editor.State.Selection);
    }
    [Fact] public void HistoryBudgetRetainsWholeCommandsAndRejectsOversizeBeforeClearingRedo()
    {
        var history = new EditHistory(2, 1800); var state = DocumentState.Empty;
        for (var i = 0; i < 3; i++)
        {
            var roi = new Roi(Guid.NewGuid(), "roi", new(new(i * 100, 0), 10, 10, 0), null, false, i);
            var next = state with { Rois = state.Rois.Add(roi.Id, roi) };
            history.Push(new("add", state, next)); state = next;
        }
        state = history.Undo(state); state = history.Undo(state);
        Assert.Single(state.Rois); Assert.False(history.CanUndo); Assert.True(history.CanRedo);
        var huge = state with { Rois = state.Rois.ToImmutableDictionary(pair => pair.Key, pair => pair.Value with { Name = new string('X', 2000) }) };
        Assert.Throws<InvalidOperationException>(() => history.Push(new("too large", state, huge)));
        Assert.True(history.CanRedo); Assert.Equal(2, history.Redo(state).Rois.Count);
    }
}
