using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Interaction;
using VisionGeometry;

namespace TeachingToolWpf.Core.Tests;

public class InteractionLifecycleTests
{
    [Fact]
    public void ReleaseUsesFinalPointerAndCommitsAfterEndingDrag()
    {
        var editor = new EditorSession();
        var roiId = editor.Add(new(new(100, 100), 80, 80, 0));
        InteractionController? input = null;
        var commitCount = 0;
        input = new InteractionController(editor, commitPreview: name =>
        {
            Assert.False(input!.IsDragging);
            Assert.True(editor.IsPreviewing);
            commitCount++;
            editor.CommitPreview(name);
        });

        input.Down(new(100, 100));
        input.Move(new(110, 120));
        input.Up(new(140, 150));

        Assert.Equal(1, commitCount);
        Assert.Equal(new PointD(140, 150), editor.State.Rois[roiId].Geometry.Center);
        Assert.False(editor.IsPreviewing);
        editor.Undo();
        Assert.Equal(new PointD(100, 100), editor.State.Rois[roiId].Geometry.Center);
    }

    [Fact]
    public void FailedCommitCancelsPreviewAndClearsGestureWithoutAddingHistory()
    {
        var editor = new EditorSession();
        var roiId = editor.Add(new(new(100, 100), 80, 80, 0));
        var previousUndoName = editor.UndoName;
        var input = new InteractionController(editor, commitPreview: _ => throw new InvalidOperationException("commit failed"));
        input.Down(new(100, 100));
        input.Move(new(150, 160));

        var error = Assert.Throws<InvalidOperationException>(() => input.Up(new(170, 180)));

        Assert.Equal("commit failed", error.Message);
        Assert.False(input.IsDragging);
        Assert.False(editor.IsPreviewing);
        Assert.Equal(new InteractionOverlay(), input.Overlay);
        Assert.Equal(new PointD(100, 100), editor.State.Rois[roiId].Geometry.Center);
        Assert.Equal(previousUndoName, editor.UndoName);
        editor.Undo();
        Assert.Empty(editor.State.Rois);
    }

    [Theory]
    [InlineData(InputModifiers.None)]
    [InlineData(InputModifiers.Control)]
    [InlineData(InputModifiers.Shift)]
    [InlineData(InputModifiers.Alt)]
    public void NavigationOnlyStillAllowsMiddleButtonPanWithoutEditing(InputModifiers modifiers)
    {
        var editor = new EditorSession();
        var roiId = editor.Add(new(new(100, 100), 80, 80, 0));
        var previousUndoName = editor.UndoName;
        var input = new InteractionController(editor) { NavigationOnly = true, Tool = EditorTool.Draw };

        input.Down(new(100, 100), modifiers, middle: true);
        input.Move(new(140, 160), modifiers);
        input.Up(new(140, 160), modifiers);

        Assert.Equal(new PointD(40, 60), editor.State.Viewport.Offset);
        Assert.Equal(new PointD(100, 100), editor.State.Rois[roiId].Geometry.Center);
        Assert.Equal(previousUndoName, editor.UndoName);
        Assert.False(editor.IsPreviewing);
    }
}
