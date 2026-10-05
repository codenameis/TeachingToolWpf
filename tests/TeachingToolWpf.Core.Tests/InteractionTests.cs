using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Geometry;
using VisionCanvas.Core.Interaction;
using VisionGeometry;

namespace TeachingToolWpf.Core.Tests;

public class InteractionTests
{
    [Fact]
    public void DragIsSingleUndoAndCancelRestoresBaseline()
    {
        var editor = new EditorSession();
        var id = editor.Add(new(new(100, 100), 80, 80, 0));
        var input = new InteractionController(editor);
        input.Down(new(100, 100));
        input.Move(new(120, 120));
        input.Move(new(150, 130));
        input.Up(new(150, 130));
        Assert.Equal(new PointD(150, 130), editor.State.Rois[id].Geometry.Center);
        editor.Undo();
        Assert.Equal(new PointD(100, 100), editor.State.Rois[id].Geometry.Center);
        input.Down(new(100, 100));
        input.Move(new(200, 200));
        input.Cancel();
        Assert.Equal(new PointD(100, 100), editor.State.Rois[id].Geometry.Center);
        Assert.True(editor.CanRedo);
    }

    [Fact]
    public void MarqueeAddsMultipleAndDrawingCreatesExplicitChild()
    {
        var editor = new EditorSession();
        var parent = editor.Add(new(new(200, 200), 300, 300, 0));
        var input = new InteractionController(editor) { Tool = EditorTool.Draw, DrawParentId = parent };
        input.Down(new(100, 100));
        input.Move(new(140, 150));
        input.Up(new(140, 150));
        var child = editor.State.Selection.Single();
        Assert.Equal(parent, editor.State.Rois[child].ParentId);
        input.Tool = EditorTool.Select;
        input.Down(new(0, 0));
        input.Move(new(400, 400));
        input.Up(new(400, 400));
        Assert.Equal(2, editor.State.Selection.Count);
    }

    [Fact]
    public void RotatedResizeKeepsOppositeCornerFixed()
    {
        var original = new RoiGeometry(new(100, 100), 40, 80, 33);
        var resized = HandleGeometry.Resize(original, 0, original.ToWorld(new(-50, -70)));
        Assert.True((original.FrameCorners[2] - resized.FrameCorners[2]).Length < 1e-9);
        Assert.Equal(70, resized.Width, 8);
        Assert.Equal(110, resized.Height, 8);
    }
}
