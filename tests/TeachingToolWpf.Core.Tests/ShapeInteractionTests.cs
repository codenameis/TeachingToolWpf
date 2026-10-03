using TeachingToolWpf.Core.Editing;
using TeachingToolWpf.Core.Geometry;
using TeachingToolWpf.Core.Interaction;

namespace TeachingToolWpf.Core.Tests;

public class ShapeInteractionTests
{
    [Theory]
    [InlineData(RoiShapeKind.Circle)]
    [InlineData(RoiShapeKind.Triangle)]
    [InlineData(RoiShapeKind.Rectangle)]
    public void DragCreatesChosenShapeWithOneUndo(RoiShapeKind kind)
    {
        var editor = new EditorSession();
        editor.InitializeImage(new("image", "image", 200, 200), ViewportTransform.Identity);
        var input = new InteractionController(editor) { DrawShape = kind, Tool = EditorTool.Draw };
        input.Down(new(-10, -10));
        input.Move(new(90, 70));
        input.Up(new(90, 70));
        Assert.Equal(kind, Assert.Single(editor.State.Rois).Value.Geometry.Shape.Kind);
        Assert.True(RoiImageBounds.ContainsAll(editor.State));
        Assert.Equal(EditorTool.Select, input.Tool);
        editor.Undo();
        Assert.Empty(editor.State.Rois);
        editor.Redo();
        Assert.Single(editor.State.Rois);
    }

    [Fact]
    public void PolygonClicksRemainDraftUntilCompleteAndCancelPreservesRedo()
    {
        var editor = new EditorSession();
        editor.Add(new(new(50, 50), 10, 10, 0));
        editor.Undo();
        var input = new InteractionController(editor) { DrawShape = RoiShapeKind.Polygon, Tool = EditorTool.Draw };
        void Click(PointD p)
        {
            input.Down(p);
            input.Up(p);
        }
        Click(new(10, 10));
        Click(new(110, 10));
        Click(new(110, 110));
        Click(new(55, 55));
        Click(new(10, 110));
        Assert.Empty(editor.State.Rois);
        Assert.Equal(5, input.PolygonPointCount);
        Assert.True(editor.CanRedo);
        input.Cancel();
        Assert.Equal(0, input.PolygonPointCount);
        Assert.True(editor.CanRedo);
        Click(new(10, 10));
        Click(new(110, 10));
        Click(new(110, 110));
        input.RemovePolygonPoint();
        Assert.False(input.CanCompletePolygon);
        Click(new(60, 110));
        input.CompletePolygon();
        Assert.Equal(RoiShapeKind.Polygon, Assert.Single(editor.State.Rois).Value.Geometry.Shape.Kind);
        Assert.Equal(EditorTool.Select, input.Tool);
        Assert.False(editor.CanRedo);
        editor.Undo();
        Assert.Empty(editor.State.Rois);
    }

    [Fact]
    public void InvalidPolygonAndSourceReplacementDoNotCommitDraft()
    {
        var editor = new EditorSession();
        var input = new InteractionController(editor) { DrawShape = RoiShapeKind.Polygon, Tool = EditorTool.Draw };
        void Click(PointD p)
        {
            input.Down(p);
            input.Up(p);
        }
        Click(new(10, 10));
        Click(new(110, 110));
        Click(new(10, 110));
        Click(new(110, 10));
        Assert.Throws<ArgumentException>(input.CompletePolygon);
        Assert.Empty(editor.State.Rois);
        Assert.Equal(EditorTool.Draw, input.Tool);
        editor.SetImage(new("next", "next", 200, 200), ViewportTransform.Identity);
        input.CompletePolygon();
        Assert.Empty(editor.State.Rois);
        Assert.Equal(0, input.PolygonPointCount);
    }
}
