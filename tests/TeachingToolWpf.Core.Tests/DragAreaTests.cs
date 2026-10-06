using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Interaction;
using VisionGeometry;

namespace TeachingToolWpf.Core.Tests;

public sealed class DragAreaTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DrawAreaTracksMouseRectangleAndDisappearsOnReleaseOrCancel(bool cancel)
    {
        var editor = new EditorSession();
        editor.InitializeImage(new("drag", "drag", 100, 100), new(2, new(10, 20)));
        var input = new InteractionController(editor) { Tool = EditorTool.Draw, DrawShape = RoiShapeKind.Circle };
        var before = editor.State;
        input.Down(new(30, 40));
        input.Move(new(90, 140));
        Assert.Equal(new BoundsD(30, 40, 90, 140), input.Overlay.DragArea);
        Assert.Equal(30, input.Overlay.Draft!.Width);
        Assert.Same(before, editor.State);
        if (cancel)
        {
            input.Cancel();
            Assert.Same(before, editor.State);
        }
        else
        {
            input.Up(new(90, 140));
            Assert.Single(editor.State.Rois);
            editor.Undo();
            Assert.Empty(editor.State.Rois);
        }
        Assert.Null(input.Overlay.DragArea);
        Assert.Null(input.Overlay.Draft);
    }
}
