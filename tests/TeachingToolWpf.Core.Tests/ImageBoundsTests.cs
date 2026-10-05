using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Geometry;
using VisionCanvas.Core.Interaction;
using VisionGeometry;

namespace TeachingToolWpf.Core.Tests;

public class ImageBoundsTests
{
    [Fact]
    public void ZeroResetFitsWideDiagonalRoiAndItsChildAndUndoRestoresOriginal()
    {
        var editor = Create();
        var parent = editor.Add(new(new(50, 50), 130, 10, 45));
        var child = editor.AddChild(parent, new(new(50, 50), 10, 4, 45));
        editor.Select([parent]);
        var before = editor.State.Rois[parent].Geometry;
        editor.ResetAngles();
        Assert.Equal(0, editor.State.Rois[parent].Geometry.Angle);
        Assert.Equal(0, editor.State.Rois[child].Geometry.Angle);
        Assert.True(RoiImageBounds.ContainsAll(editor.State));
        Assert.Equal(parent, editor.State.Rois[child].ParentId);
        editor.Undo();
        Assert.Equal(before, editor.State.Rois[parent].Geometry);
    }

    private static EditorSession Create()
    {
        var editor = new EditorSession();
        editor.InitializeImage(new("sample:100:100", "Image", 100, 100), new(1, new()));
        return editor;
    }

    [Fact]
    public void ReplacingEvenSameImageClearsRoisAndUndoRestoresEverything()
    {
        var editor = Create();
        editor.Add(new(new(50, 50), 20, 20, 0));
        var before = editor.State;
        editor.SetImage(before.Image!, new(2, new(50, 70)));
        Assert.Empty(editor.State.Rois);
        Assert.Empty(editor.State.Selection);
        editor.Undo();
        Assert.Equal(before.Image, editor.State.Image);
        Assert.Equal(before.Viewport, editor.State.Viewport);
        Assert.Equal(before.Rois.Values, editor.State.Rois.Values);
        Assert.True(before.Selection.SetEquals(editor.State.Selection));
        editor.Redo();
        Assert.Empty(editor.State.Rois);
    }

    [Fact]
    public void DrawingPastEdgeCreatesOnlyIntersectionAndOutsideCreatesNothing()
    {
        var editor = Create();
        var input = new InteractionController(editor) { Tool = EditorTool.Draw };
        input.Down(new(40, 30));
        input.Up(new(170, 190));
        var roi = editor.State.Rois.Values.Single();
        Assert.Equal(new RoiGeometry(new(70, 65), 60, 70, 0), roi.Geometry);
        input.Tool = EditorTool.Draw;
        input.Down(new(120, 120));
        input.Up(new(190, 180));
        Assert.Single(editor.State.Rois);
    }

    [Fact]
    public void GroupTranslationStopsAtBoundaryWithoutChangingSpacing()
    {
        var editor = Create();
        var a = editor.Add(new(new(20, 20), 20, 20, 0));
        var b = editor.Add(new(new(80, 80), 20, 20, 0));
        editor.Select([a, b]);
        editor.TranslateSelection(new(100, 100));
        Assert.True(RoiImageBounds.ContainsAll(editor.State));
        Assert.Equal(90, editor.State.Rois[b].Geometry.Center.X, 5);
        Assert.Equal(60, (editor.State.Rois[b].Geometry.Center - editor.State.Rois[a].Geometry.Center).X, 5);
        editor.Undo();
        Assert.Equal(new PointD(20, 20), editor.State.Rois[a].Geometry.Center);
    }

    [Fact]
    public void ResizeRotationPropertiesAndGroupingStayInsideImage()
    {
        var editor = Create();
        var a = editor.Add(new(new(20, 20), 35, 30, 0));
        editor.BeginPreview();
        editor.PreviewResize(a, new(new(50, 50), 200, 200, 0));
        editor.CommitPreview("Resize");
        Assert.True(RoiImageBounds.ContainsAll(editor.State));
        editor.Undo();
        editor.RotateSelection(45, editor.State.Rois[a].Geometry.Center);
        Assert.True(RoiImageBounds.ContainsAll(editor.State));
        editor.SetProperties(a, "Border", new(new(-50, 30), 40, 30, 70));
        Assert.True(RoiImageBounds.ContainsAll(editor.State));
        var b = editor.Add(new(new(95, 95), 30, 30, 0));
        editor.Select([a, b]);
        editor.GroupSelection();
        Assert.True(RoiImageBounds.ContainsAll(editor.State));
    }
}
