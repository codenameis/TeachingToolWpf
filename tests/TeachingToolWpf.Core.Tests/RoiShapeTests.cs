using TeachingToolWpf.Core.Editing;
using TeachingToolWpf.Core.Geometry;

namespace TeachingToolWpf.Core.Tests;

public class RoiShapeTests
{
    [Fact]
    public void CircleUsesRadiusForHitContainmentAndBounds()
    {
        var circle = RoiGeometry.Create(RoiShapeKind.Circle, new(50, 50), 100, 100);
        Assert.False(circle.Contains(new PointD(95, 95)));
        Assert.True(circle.Contains(new PointD(50, 0)));
        Assert.Equal(Math.PI * 2500, circle.Area, 8);
        Assert.Equal(circle.Bounds, circle.RotateAround(circle.Center, 45).Bounds);
        Assert.False(circle.Contains(new RoiGeometry(new(80, 80), 15, 15, 0)));
        Assert.True(circle.Contains(RoiGeometry.Create(RoiShapeKind.Circle, new(90, 50), 20, 20)));
    }

    [Fact]
    public void ConcaveParentRejectsCrossingEdgesEvenWhenVerticesAreInside()
    {
        var parent = RoiGeometry.Polygon([new(0, 0), new(100, 0), new(100, 100), new(70, 100),
            new(70, 30), new(30, 30), new(30, 100), new(0, 100)]);
        var child = new RoiGeometry(new(50, 70), 80, 10, 0);
        Assert.All(child.Outline, p => Assert.True(parent.Contains(p)));
        Assert.False(parent.Contains(child));
        Assert.False(parent.Contains(new PointD(50, 50)));
        Assert.False(parent.Contains(RoiGeometry.Create(RoiShapeKind.Circle, new(20, 60), 30, 30)));
        Assert.True(parent.Contains(RoiGeometry.Create(RoiShapeKind.Circle, new(15, 60), 20, 20)));
    }

    [Fact]
    public void TriangleHitAndHierarchyUseActualShape()
    {
        var session = new EditorSession();
        var triangle = session.Add(RoiGeometry.Create(RoiShapeKind.Triangle, new(50, 50), 100, 100));
        var outside = session.Add(new(new(10, 10), 4, 4, 0));
        var inside = session.Add(RoiGeometry.Create(RoiShapeKind.Circle, new(50, 70), 10, 10));
        Assert.Null(session.State.Rois[outside].ParentId);
        Assert.Equal(triangle, session.State.Rois[inside].ParentId);
        Assert.Null(session.State.Snapshot.Spatial.HitTest(new(5, 5), 0));
        session.Select([triangle]);
        var before = session.State;
        session.DeleteSelection();
        Assert.Single(session.State.Rois);
        session.Undo();
        Assert.Equal(before.Rois, session.State.Rois);
        Assert.Equal(before.Selection, session.State.Selection);
        session.Redo();
        Assert.Single(session.State.Rois);
    }

    [Fact]
    public void TransformCopyPasteAndHistoryRetainShapeAndVertices()
    {
        var session = new EditorSession();
        session.InitializeImage(new("shape", "1", 500, 500), new(1, new()));
        var polygon = RoiGeometry.Polygon([new(30, 30), new(130, 30), new(90, 65), new(130, 130), new(30, 130)]);
        var id = session.Add(polygon);
        session.RotateSelection(25, polygon.Center);
        session.TranslateSelection(new(20, 15));
        var changed = session.State.Rois[id].Geometry;
        Assert.Same(polygon.Shape, changed.Shape);
        session.ResetAngles();
        Assert.Same(polygon.Shape, session.State.Rois[id].Geometry.Shape);
        var clipboard = new RoiClipboard();
        clipboard.Copy(session.State);
        session.Paste(clipboard);
        var pasted = session.State.Selection.Single();
        Assert.NotEqual(id, pasted);
        Assert.Same(polygon.Shape, session.State.Rois[pasted].Geometry.Shape);
        var after = session.State;
        session.Undo();
        session.Redo();
        Assert.Equal(after.Rois, session.State.Rois);
        session.Undo();
        session.TranslateSelection(new(1, 0));
        Assert.False(session.CanRedo);
    }

    [Fact]
    public void CircleRotationAtImageEdgeRemainsUnboundedAroundItsCenter()
    {
        var session = new EditorSession();
        session.InitializeImage(new("shape", "1", 100, 100), new(1, new()));
        var id = session.Add(RoiGeometry.Create(RoiShapeKind.Circle, new(50, 50), 100, 100));
        Assert.Equal(RotationRange.Unbounded, session.State.TransformSelection.RotationLimits(new(50, 50)));
        session.RotateSelection(45, new(50, 50));
        Assert.Equal(45, session.State.Rois[id].Geometry.Angle);
        Assert.True(RoiImageBounds.ContainsAll(session.State));
        for (var handle = 0;
handle < 8;
handle++)
        {
            var resized = HandleGeometry.Resize(session.State.Rois[id].Geometry, handle, new(33, 33));
            Assert.Equal(resized.Width, resized.Height);
            Assert.Equal(RoiShapeKind.Circle, resized.Shape.Kind);
        }
    }

    [Fact]
    public void CircleResizeKeepsOneDiameterAtFractionalCoordinates()
    {
        var circle = RoiGeometry.Create(RoiShapeKind.Circle, new(24.39, 75.57), 13.7, 13.7).RotateAround(new(24.39, 75.57), 22.7);
        for (var handle = 0;
handle < 8;
handle++)
        {
            for (var i = 0;
i < 100;
i++)
            {
                var resized = HandleGeometry.Resize(circle, handle, new(i * 1.3789, i * .7917));
                Assert.Equal(resized.Width, resized.Height);
            }
        }
    }

    [Fact]
    public void InvalidPolygonsCannotEnterHistory()
    {
        Assert.Throws<ArgumentException>(() => RoiGeometry.Polygon([new(0, 0), new(10, 10), new(0, 10), new(10, 0)]));
        Assert.Throws<ArgumentException>(() => RoiGeometry.Polygon([new(0, 0), new(10, 0), new(20, 0)]));
        Assert.Throws<ArgumentException>(() => RoiGeometry.Polygon([new(0, 0), new(10, 0), new(10, 0), new(0, 10)]));
        Assert.Throws<ArgumentException>(() => RoiGeometry.Polygon([new(0, 0), new(double.NaN, 0), new(0, 10)]));
        Assert.Throws<ArgumentException>(() => RoiGeometry.Polygon([new(0, 0), new(10, 0), new(5, 0), new(10, 10), new(0, 10)]));
    }

    [Fact]
    public void ConcaveChildCanNestEvenWhenItsFrameCenterIsOutsideTheParent()
    {
        var parent = RoiGeometry.Polygon([new(0, 0), new(100, 0), new(100, 100), new(70, 100), new(70, 30), new(30, 30), new(30, 100), new(0, 100)]);
        var child = RoiGeometry.Polygon([new(5, 5), new(95, 5), new(95, 95), new(75, 95), new(75, 25), new(25, 25), new(25, 95), new(5, 95)]);
        Assert.False(parent.Contains(child.Center));
        Assert.True(parent.Contains(child));
        var editor = new EditorSession();
        var parentId = editor.Add(parent);
        var childId = editor.Add(child);
        Assert.Equal(parentId, editor.State.Rois[childId].ParentId);
    }

    [Theory]
    [InlineData(RoiShapeKind.Circle)]
    [InlineData(RoiShapeKind.Triangle)]
    [InlineData(RoiShapeKind.Polygon)]
    public void ShapesStayInsideImageThroughResizeRotationAndPasteToSmallerImage(RoiShapeKind kind)
    {
        var editor = new EditorSession();
        editor.InitializeImage(new("image", "image", 100, 100), ViewportTransform.Identity);
        var id = editor.Add(RoiGeometry.Create(kind, new(70, 40), 50, 50));
        var original = editor.State.Rois[id].Geometry;
        editor.SetGeometry(id, original.WithFrame(new(95, 95), 120, 120, 0));
        Assert.True(RoiImageBounds.ContainsAll(editor.State));
        Assert.Equal(kind, editor.State.Rois[id].Geometry.Shape.Kind);
        editor.Undo();
        editor.BeginPreview();
        editor.PreviewRotation(new(50, 50), 170);
        var preview = editor.TransformPreview!;
        Assert.True(RoiImageBounds.Fits(preview.Geometry(editor.State.Rois[id]).Bounds, editor.State.Image!));
        editor.CancelPreview();
        Assert.Equal(original, editor.State.Rois[id].Geometry);
        var clipboard = new RoiClipboard();
        clipboard.Copy(editor.State);
        editor.SetImage(new("smaller", "smaller", 20, 20), ViewportTransform.Identity);
        editor.Paste(clipboard);
        Assert.True(RoiImageBounds.ContainsAll(editor.State));
        Assert.Equal(kind, editor.State.Rois.Values.Single().Geometry.Shape.Kind);
    }
}
