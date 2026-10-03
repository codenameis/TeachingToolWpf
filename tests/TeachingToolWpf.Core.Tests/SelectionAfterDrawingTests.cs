using TeachingToolWpf.Core.Editing;
using TeachingToolWpf.Core.Geometry;
using TeachingToolWpf.Core.Interaction;

namespace TeachingToolWpf.Core.Tests;

public class SelectionAfterDrawingTests
{
    [Fact]
    public void ClickWithoutMovingOnlyPublishesSelectionAndPreservesRedo()
    {
        var editor = new EditorSession();
        var first = editor.Add(new(new(100, 100), 80, 80, 0));
        editor.Add(new(new(300, 300), 80, 80, 0));
        editor.Rename(first, "renamed");
        editor.Undo();
        var geometry = editor.State.Rois;
        var notifications = 0;
        editor.Changed += (_, _) => notifications++;
        var input = new InteractionController(editor);
        input.Down(new(100, 100));
        input.Up(new(100, 100));
        Assert.Equal(1, notifications);
        Assert.Same(geometry, editor.State.Rois);
        Assert.True(editor.CanRedo);
        Assert.Equal(first, Assert.Single(editor.State.Selection));
        editor.Select([first]);
        Assert.Equal(1, notifications);
    }

    [Fact]
    public void CompletedDrawingReturnsToSelectionAndNextClickSelectsAnotherRoi()
    {
        var editor = new EditorSession();
        var first = editor.Add(new(new(100, 100), 80, 80, 0));
        var input = new InteractionController(editor) { Tool = EditorTool.Draw };

        input.Down(new(300, 300));
        input.Move(new(400, 400));
        input.Up(new(400, 400));
        var second = editor.State.Selection.Single();
        Assert.Equal(EditorTool.Select, input.Tool);
        Assert.False(input.IsDragging);
        Assert.NotEqual(first, second);
        var beforeClick = editor.State.Rois;
        var historyBytes = editor.HistoryBytes;

        input.Down(new(100, 100));
        input.Up(new(100, 100));

        Assert.Equal(first, Assert.Single(editor.State.Selection));
        Assert.Equal(beforeClick[first], editor.State.Rois[first]);
        Assert.Equal(beforeClick[second], editor.State.Rois[second]);
        Assert.Equal(historyBytes, editor.HistoryBytes);
        editor.Undo();
        Assert.Equal(first, Assert.Single(editor.State.Rois).Key);
        editor.Redo();
        Assert.Equal(second, Assert.Single(editor.State.Selection));
        input.Down(new(100, 100));
        input.Up(new(100, 100));
        Assert.Equal(first, Assert.Single(editor.State.Selection));
    }

    [Fact]
    public void DrawToolCanBeReactivatedToCreateAContainedChild()
    {
        var editor = new EditorSession();
        var input = new InteractionController(editor) { Tool = EditorTool.Draw };
        input.Down(new(50, 50));
        input.Up(new(400, 400));
        var parent = Assert.Single(editor.State.Selection);
        Assert.Equal(EditorTool.Select, input.Tool);

        input.Tool = EditorTool.Draw;
        input.Down(new(100, 100));
        input.Up(new(150, 150));

        var child = Assert.Single(editor.State.Selection);
        Assert.Equal(parent, editor.State.Rois[child].ParentId);
        Assert.Equal(EditorTool.Select, input.Tool);
    }

    [Fact]
    public void CanceledOrEmptyDrawingKeepsDrawToolReadyWithoutHistoryChanges()
    {
        var editor = new EditorSession();
        var original = editor.Add(new(new(100, 100), 80, 80, 0));
        var input = new InteractionController(editor) { Tool = EditorTool.Draw };
        var historyBytes = editor.HistoryBytes;

        input.Down(new(300, 300));
        input.Move(new(400, 400));
        input.Cancel();
        Assert.Equal(EditorTool.Draw, input.Tool);
        Assert.False(input.IsDragging);
        Assert.Null(input.Overlay.Draft);

        input.Down(new(300, 300));
        input.Up(new(300, 300));
        Assert.Equal(EditorTool.Draw, input.Tool);
        Assert.Equal(original, Assert.Single(editor.State.Selection));
        Assert.Single(editor.State.Rois);
        Assert.Equal(historyBytes, editor.HistoryBytes);
    }
}
