using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Geometry;
using VisionCanvas.Core.Interaction;
using VisionGeometry;

namespace TeachingToolWpf.Core.Tests;

public class DoubleClickSelectionTests
{
    [Fact]
    public void NativeClickSequenceCyclesNestedHierarchyAndKeepsPickedGroupForDragging()
    {
        var editor = new EditorSession();
        var root = editor.AddParent(new(new(500, 500), 600, 600, 0));
        var module = editor.AddChild(root, new(new(500, 500), 400, 400, 0));
        var group = editor.AddChild(module, new(new(500, 500), 200, 200, 0));
        var leaf = editor.AddChild(group, new(new(500, 500), 60, 60, 0));
        var input = new InteractionController(editor);
        var before = editor.State.Rois;
        var history = editor.HistoryBytes;
        var point = new PointD(500, 500);
        foreach (var expected in new[] { group, module, root, leaf, group })
        {
            input.Down(point);
            input.Up(point);
            input.DoubleClick(point);
            input.Up(point);
            Assert.Equal(expected, Assert.Single(editor.State.Selection));
            Assert.Same(before, editor.State.Rois);
            Assert.Equal(history, editor.HistoryBytes);
        }
        input.Down(point);
        input.Move(point + new PointD(30, 0));
        input.Up(point + new PointD(30, 0));
        Assert.Equal(group, Assert.Single(editor.State.Selection));
        Assert.Equal(before[group].Geometry.Translate(new(30, 0)), editor.State.Rois[group].Geometry);
        Assert.Equal(before[leaf].Geometry.Translate(new(30, 0)), editor.State.Rois[leaf].Geometry);
        Assert.Equal(before[module], editor.State.Rois[module]);
        editor.Undo();
        Assert.Equal(before, editor.State.Rois);
    }

    [Fact]
    public void ControlClickStillSelectsInnerRoiWhileGroupIsSelected()
    {
        var editor = new EditorSession();
        var group = editor.AddParent(new(new(100, 100), 100, 100, 0));
        var leaf = editor.AddChild(group, new(new(100, 100), 20, 20, 0));
        editor.Select([group]);
        var input = new InteractionController(editor);
        input.Down(new(100, 100), InputModifiers.Control);
        input.Up(new(100, 100), InputModifiers.Control);
        Assert.True(editor.State.Selection.SetEquals([group, leaf]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NormalBatchHitKeepsNearestCellAfterPartialEdits(bool split)
    {
        var editor = new EditorSession();
        editor.SetImage(new("image", "image", 1000, 1000), ViewportTransform.Identity);
        var ids = editor.AddRange(new RoiGeometry[] { new(new(195, 200), 6.5, 6.5, 0), new(new(205, 200), 6.5, 6.5, 0) }, selectAdded: true);
        if (split)
        {
            editor.Select([ids[0]]);
            Move(10);
            Move(-10);
        }
        editor.Select([]);
        var input = new InteractionController(editor);
        input.DoubleClick(new(198, 200));
        Assert.Equal(ids[0], Assert.Single(editor.State.Selection));

        void Move(double x)
        {
            editor.BeginPreview();
            editor.PreviewTranslation(new(x, 0));
            editor.CommitPreview("move");
        }
    }

    [Fact]
    public void AltClickCyclesOverlappingBatchAndOrdinaryRoi()
    {
        var editor = new EditorSession();
        editor.SetImage(new("image", "image", 1000, 1000), ViewportTransform.Identity);
        var normal = editor.Add(new(new(200, 200), 80, 80, 0));
        var first = editor.AddRange([new RoiGeometry(new(200, 200), 65, 65, 0)], selectAdded: true).Single();
        var second = editor.AddRange([new RoiGeometry(new(200, 200), 65, 65, 0)], selectAdded: true).Single();
        editor.Select([]);
        var input = new InteractionController(editor);
        var before = editor.State.Rois;
        var history = editor.HistoryBytes;
        Click();
        Assert.Equal(second, Assert.Single(editor.State.Selection));
        Click();
        Assert.Equal(first, Assert.Single(editor.State.Selection));
        Click();
        Assert.Equal(normal, Assert.Single(editor.State.Selection));
        Click();
        Assert.Equal(second, Assert.Single(editor.State.Selection));
        Assert.Same(before, editor.State.Rois);
        Assert.Equal(history, editor.HistoryBytes);

        void Click()
        {
            input.Down(new(200, 200), InputModifiers.Alt);
            input.Up(new(200, 200), InputModifiers.Alt);
        }
    }

    [Fact]
    public void DoubleClickSelectsHitRoiInsteadOfPreviousSelectionHandle()
    {
        var editor = new EditorSession();
        var previous = editor.Add(new(new(100, 100), 80, 80, 0));
        var target = editor.Add(new(new(64, 64), 12, 12, 25));
        editor.Select([previous]);
        var input = new InteractionController(editor);
        var before = editor.State.Rois;
        var historyBytes = editor.HistoryBytes;

        input.Down(new(64, 64));
        input.Up(new(64, 64));
        input.DoubleClick(new(64, 64));
        input.Move(new(80, 80));
        input.Up(new(80, 80));

        Assert.Equal(target, Assert.Single(editor.State.Selection));
        Assert.Same(before, editor.State.Rois);
        Assert.Equal(historyBytes, editor.HistoryBytes);
    }

    [Fact]
    public void DoubleClickNearRotatedCornerStillStartsResizeWhenTargetIsAlreadySelected()
    {
        var editor = new EditorSession();
        var id = editor.Add(new(new(100, 100), 80, 40, 30));
        var input = new InteractionController(editor);
        var corner = HandleGeometry.ResizeHandles(editor.State.Rois[id].Geometry, editor.State.Viewport)[0];

        input.DoubleClick(corner);
        input.Move(corner + new PointD(18, 12));
        input.Up(corner + new PointD(18, 12));

        Assert.NotEqual(80, editor.State.Rois[id].Geometry.Width);
        Assert.Equal("ROI 크기 조절", editor.UndoName);
    }

    [Fact]
    public void DoubleClickInEmptySpaceLeavesSelectionUntouched()
    {
        var editor = new EditorSession();
        var id = editor.Add(new(new(100, 100), 40, 40, 0));
        var input = new InteractionController(editor);
        var before = editor.State.Rois;

        input.DoubleClick(new(500, 500));
        input.Move(new(540, 540));
        input.Up(new(540, 540));

        Assert.Equal(id, Assert.Single(editor.State.Selection));
        Assert.Same(before, editor.State.Rois);
    }

    [Fact]
    public void DoubleClickSelectsSingleRoiFromMultipleSelectionWithoutDraggingSelectionBounds()
    {
        var editor = new EditorSession();
        var a = editor.Add(new(new(100, 100), 40, 40, 0));
        var b = editor.Add(new(new(200, 100), 40, 40, 0));
        editor.SelectAll();
        var input = new InteractionController(editor);
        var before = editor.State.Rois;

        input.DoubleClick(new(100, 100));
        input.Move(new(130, 130));
        input.Up(new(130, 130));

        Assert.Equal(a, Assert.Single(editor.State.Selection));
        Assert.NotInRange(editor.State.Selection.Count, 2, int.MaxValue);
        Assert.Same(before, editor.State.Rois);
    }
}
