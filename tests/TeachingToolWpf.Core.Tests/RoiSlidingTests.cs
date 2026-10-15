using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Interaction;
using VisionGeometry;

namespace TeachingToolWpf.Core.Tests;

public class RoiSlidingTests
{
    [Fact]
    public void PointerBeyondTheImageStillFollowsTheParentWall()
    {
        var editor = new EditorSession();
        editor.InitializeImage(new("test", "test", 1000, 1000), new(1, default));
        var parent = editor.AddParent(new(new(500, 500), 200, 200, 0));
        var child = editor.AddChild(parent, new(new(500, 500), 20, 20, 0));
        editor.BeginPreview();
        editor.PreviewTranslation(new(5000, 60));
        var shown = editor.TransformPreview!.Geometry(editor.State.Rois[child]);
        Assert.InRange(shown.Center.X, 589.99999, 590.00001);
        Assert.InRange(shown.Center.Y, 559.99999, 560.00001);
        Assert.True(editor.State.Rois[parent].Geometry.Covers(shown));
    }

    [Fact]
    public void MouseUpAtTheSameWallPositionPreservesTheExactPreview()
    {
        var editor = new EditorSession();
        var parent = editor.AddParent(new(new(4096, 4096), 3500, 3500, 0));
        var child = editor.AddChild(parent, new(new(4096, 4096), 1500, 1500, 0));
        var input = new InteractionController(editor);
        input.Down(new(4096, 4096));
        input.Move(new(7096, 4696));
        var shown = editor.TransformPreview!.Geometry(editor.State.Rois[child]);
        input.Up(new(7096, 4696));
        Assert.Equal(shown, editor.State.Rois[child].Geometry);
    }

    [Fact]
    public void ShiftDragKeepsItsAxisAtARotatedWall()
    {
        var editor = new EditorSession();
        var parent = editor.AddParent(new(new(200, 200), 200, 200, 37));
        var child = editor.AddChild(parent, new(new(200, 200), 20, 20, 37));
        var input = new InteractionController(editor);
        input.Down(new(200, 200));
        input.Move(new(500, 220), InputModifiers.Shift);
        var shown = editor.TransformPreview!.Geometry(editor.State.Rois[child]);
        Assert.Equal(200, shown.Center.Y);
        Assert.True(editor.State.Rois[parent].Geometry.Covers(shown));
    }

    [Fact]
    public async Task PreparedSlidePreservesMultipleParentsAndExactPreview()
    {
        var editor = new EditorSession();
        var a = editor.AddParent(new(new(200, 200), 200, 200, 0));
        var first = editor.AddChild(a, new(new(200, 200), 20, 20, 0));
        var b = editor.AddParent(new(new(500, 200), 200, 200, 30));
        var second = editor.AddChild(b, new(new(500, 200), 20, 20, 30));
        editor.Select([first, second]);
        var before = editor.State;
        editor.BeginPreview();
        editor.PreviewTranslation(new(160, 60));
        var preview = editor.TransformPreview!;
        Assert.True(before.Rois[a].Geometry.Covers(preview.Geometry(before.Rois[first])));
        Assert.True(before.Rois[b].Geometry.Covers(preview.Geometry(before.Rois[second])));
        editor.CommitPrepared(await editor.PreparePreviewAsync("slide"));
        Assert.Equal(preview.Geometry(before.Rois[first]), editor.State.Rois[first].Geometry);
        Assert.Equal(preview.Geometry(before.Rois[second]), editor.State.Rois[second].Geometry);
        editor.Undo();
        Assert.Equal(before.Rois, editor.State.Rois);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(37)]
    public void DragSlidesAlongTheActualParentWallAndCommitsThePreview(double angle)
    {
        var editor = new EditorSession();
        var parent = editor.AddParent(new(new(200, 200), 200, 200, angle));
        var child = editor.AddChild(parent, new(new(200, 200), 20, 20, angle));
        editor.BeginPreview();
        foreach (var y in new[] { 20d, 50, -30 })
        {
            editor.PreviewTranslation(new PointD(180, y).Rotate(angle));
            var shown = editor.TransformPreview!.Geometry(editor.State.Rois[child]);
            var local = (shown.Center - new PointD(200, 200)).Rotate(-angle);
            Assert.InRange(local.X, 89.99999, 90.00001);
            Assert.InRange(local.Y, y - .00001, y + .00001);
            Assert.True(editor.State.Rois[parent].Geometry.Covers(shown));
        }
        var preview = editor.TransformPreview!.Geometry(editor.State.Rois[child]);
        editor.CommitPreview("slide");
        Assert.Equal(preview, editor.State.Rois[child].Geometry);
        editor.Undo();
        Assert.Equal(new PointD(200, 200), editor.State.Rois[child].Geometry.Center);
    }

    [Fact]
    public void CircleSlidesAroundItsCircularParentBoundary()
    {
        var editor = new EditorSession();
        var parent = editor.AddParent(new(new(200, 200), 200, 200, 0, RoiShape.Circle));
        var child = editor.AddChild(parent, new(new(280, 200), 20, 20, 0, RoiShape.Circle));
        editor.BeginPreview();
        editor.PreviewTranslation(new(100, 40));
        var shown = editor.TransformPreview!.Geometry(editor.State.Rois[child]);
        Assert.True(shown.Center.Y > 210);
        Assert.True(editor.State.Rois[parent].Geometry.Covers(shown));
    }

    [Fact]
    public void ConcaveWallSlidesWithoutCrossingTheGap()
    {
        var editor = new EditorSession();
        var parent = editor.AddParent(RoiGeometry.Polygon([new(0, 0), new(400, 0), new(400, 400),
            new(260, 400), new(260, 100), new(140, 100), new(140, 400), new(0, 400)]));
        var child = editor.AddChild(parent, new(new(70, 250), 40, 40, 0));
        editor.BeginPreview();
        editor.PreviewTranslation(new(260, 70));
        var shown = editor.TransformPreview!.Geometry(editor.State.Rois[child]);
        Assert.InRange(shown.Center.X, 119.99999, 120.00001);
        Assert.InRange(shown.Center.Y, 319.99999, 320.00001);
        Assert.True(editor.State.Rois[parent].Geometry.Covers(shown));
    }

    [Fact]
    public void MultipleSelectionHasNoRotationGestureAtTheFormerHandle()
    {
        var editor = new EditorSession();
        var a = editor.Add(new(new(100, 100), 30, 30, 0));
        var b = editor.Add(new(new(200, 100), 30, 30, 0));
        editor.Select([a, b]);
        var before = editor.State.Rois;
        var input = new InteractionController(editor);
        var handle = new PointD(150, 57);
        input.Down(handle);
        input.Move(new(193, 100));
        Assert.Null(editor.TransformPreview);
        input.Up(new(193, 100));
        Assert.Same(before, editor.State.Rois);
    }
}
