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
        Click(new(110, 10));
        Click(new(160, 10));
        Assert.Throws<ArgumentException>(input.CompletePolygon);
        Assert.Empty(editor.State.Rois);
        Assert.Equal(EditorTool.Draw, input.Tool);
        editor.SetImage(new("next", "next", 200, 200), ViewportTransform.Identity);
        input.CompletePolygon();
        Assert.Empty(editor.State.Rois);
        Assert.Equal(0, input.PolygonPointCount);
    }

    [Fact]
    public void CrossingClosesAtNewVertexWithOneUndoAndExactRedo()
    {
        var editor = new EditorSession();
        editor.Add(new(new(300, 300), 10, 10, 0));
        editor.Undo();
        var input = new InteractionController(editor) { DrawShape = RoiShapeKind.Polygon, Tool = EditorTool.Draw };
        Click(input, new(10, 10), new(110, 110), new(10, 110));
        input.Move(new(110, 10));
        Assert.Empty(editor.State.Rois);
        Assert.Equal(new PointD(60, 60), input.Overlay.PolygonPoints[0]);
        Assert.Equal(input.Overlay.PolygonPoints[0], input.Overlay.PolygonPoints[^1]);
        Assert.True(editor.CanRedo);
        Click(input, new PointD(110, 10));

        var roi = Assert.Single(editor.State.Rois).Value;
        Assert.Equal(3, roi.Geometry.Outline.Length);
        Assert.Contains(new PointD(60, 60), roi.Geometry.Outline);
        Assert.Equal(2500, roi.Geometry.Area, 6);
        Assert.True(roi.Geometry.Contains(new PointD(60, 90)));
        Assert.False(roi.Geometry.Contains(new PointD(60, 30)));
        Assert.Equal(EditorTool.Select, input.Tool);
        Assert.Equal(0, input.PolygonPointCount);
        Assert.False(editor.CanRedo);
        editor.Undo();
        Assert.Empty(editor.State.Rois);
        Assert.False(editor.CanUndo);
        editor.Redo();
        Assert.Equal(roi, Assert.Single(editor.State.Rois).Value);
        Assert.Equal(roi.Id, Assert.Single(editor.State.Selection));
    }

    [Fact]
    public void FirstCrossingAlongNewSegmentWinsAndOpenTailIsExcluded()
    {
        var editor = new EditorSession();
        var input = new InteractionController(editor) { DrawShape = RoiShapeKind.Polygon, Tool = EditorTool.Draw };
        Click(input, new(0, 0), new(100, 0), new(100, 100), new(20, 100),
            new(20, 20), new(80, 20), new(80, 80), new(40, 80), new(40, -20));

        var geometry = Assert.Single(editor.State.Rois).Value.Geometry;
        Assert.Equal(4, geometry.Outline.Length);
        Assert.Equal(new BoundsD(40, 20, 80, 80), geometry.Bounds);
        Assert.Equal(2400, geometry.Area, 6);
    }

    [Fact]
    public void TouchingExistingVertexClosesWithoutDuplicateVertex()
    {
        var editor = new EditorSession();
        var input = new InteractionController(editor) { DrawShape = RoiShapeKind.Polygon, Tool = EditorTool.Draw };
        Click(input, new(10, 10), new(110, 10), new(110, 110), new(10, 110), new(110, 10));

        var geometry = Assert.Single(editor.State.Rois).Value.Geometry;
        Assert.Equal(3, geometry.Outline.Length);
        Assert.Equal(5000, geometry.Area, 6);
    }

    [Fact]
    public void ExplicitCompletionAlsoClosesAtFirstIntersection()
    {
        var editor = new EditorSession();
        var input = new InteractionController(editor) { DrawShape = RoiShapeKind.Polygon, Tool = EditorTool.Draw };
        Click(input, new(10, 10), new(110, 10), new(110, 110), new(160, 110), new(160, 60));
        input.CompletePolygon();

        var geometry = Assert.Single(editor.State.Rois).Value.Geometry;
        Assert.Equal(4, geometry.Outline.Length);
        Assert.Contains(geometry.Outline, point => (point - new PointD(110, 130d / 3)).Length < 1e-6);
        Assert.Equal(110, geometry.Bounds.Left, 6);
        Assert.Equal(EditorTool.Select, input.Tool);
    }

    [Fact]
    public void FailedChildClosurePreservesDraftAndCanRetryAfterParentUndo()
    {
        var editor = new EditorSession();
        var parent = editor.Add(new(new(300, 300), 10, 10, 0));
        var input = new InteractionController(editor)
        {
            DrawShape = RoiShapeKind.Polygon,
            DrawParentId = parent,
            Tool = EditorTool.Draw
        };
        Click(input, new(10, 10), new(110, 110), new(10, 110));
        editor.DeleteSelection();
        var before = editor.State;
        Assert.Throws<ArgumentException>(() => input.Down(new(110, 10)));
        Assert.Same(before, editor.State);
        Assert.Equal(3, input.PolygonPointCount);
        Assert.Equal(EditorTool.Draw, input.Tool);
        editor.Undo();
        Click(input, new PointD(110, 10));
        Assert.Equal(parent, editor.State.Rois[editor.State.Selection.Single()].ParentId);
        editor.Select([parent]);
        editor.DeleteSelection();
        Assert.Empty(editor.State.Rois);
        editor.Undo();
        Assert.Equal(2, editor.State.Rois.Count);
    }

    [Theory]
    [InlineData(.01)]
    [InlineData(3.25)]
    public void CrossingUsesImageCoordinatesAndPrecedesFirstPointScreenSnapping(double scale)
    {
        var editor = new EditorSession();
        var view = new ViewportTransform(scale, new(-200, -150));
        editor.InitializeImage(new("24k", "24k", 24576, 24576), view);
        var input = new InteractionController(editor) { DrawShape = RoiShapeKind.Polygon, Tool = EditorTool.Draw };
        var imagePoints = new PointD[] { new(24010, 22010), new(24110, 22110), new(24010, 22110), new(24110, 22010) };
        Click(input, imagePoints.Select(view.ImageToScreen).ToArray());

        var geometry = Assert.Single(editor.State.Rois).Value.Geometry;
        Assert.Contains(geometry.Outline, point => (point - new PointD(24060, 22060)).Length < 1e-6);
        Assert.Equal(2500, geometry.Area, 5);
        Assert.True(RoiImageBounds.ContainsAll(editor.State));
    }

    [Fact]
    public void FirstPointClickCompletesOrdinaryPolygonWithoutDuplicateClosingPoint()
    {
        var editor = new EditorSession();
        var input = new InteractionController(editor) { DrawShape = RoiShapeKind.Polygon, Tool = EditorTool.Draw };
        Click(input, new(10, 10), new(110, 10), new(110, 110), new(10, 110), new(10, 10));
        var geometry = Assert.Single(editor.State.Rois).Value.Geometry;
        Assert.Equal(4, geometry.Outline.Length);
        Assert.Equal(10000, geometry.Area, 6);
        Assert.Equal(EditorTool.Select, input.Tool);
    }

    private static void Click(InteractionController input, params PointD[] points)
    {
        foreach (var point in points)
        {
            input.Down(point);
            input.Up(point);
        }
    }
}
