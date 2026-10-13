using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Geometry;
using VisionCanvas.Core.Interaction;
using VisionGeometry;

namespace TeachingToolWpf.Core.Tests;

public class DoubleClickSelectionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NormalRepeatedHitKeepsNearestCellAfterPartialEdits(bool split)
    {
        var editor = new EditorSession();
        editor.SetImage(new("image", "image", 1000, 1000), ViewportTransform.Identity);
        var id = editor.AddRepeatedGrid(new(2, 1, 10, new(200, 200)));
        if (split)
        {
            editor.SelectRepeated(new(id, 0));
            Move(10);
            Move(-10);
            Assert.Equal(2, editor.State.RepeatedRois[id].Parts.Length);
        }
        editor.Select([]);
        var input = new InteractionController(editor);
        input.DoubleClick(new(199, 200));
        Assert.Equal(new RepeatedRoiCell(id, 0), editor.State.SingleRepeatedCell);

        void Move(double x)
        {
            editor.BeginPreview();
            editor.PreviewTranslation(new(x, 0));
            editor.CommitPreview("move");
        }
    }

    [Fact]
    public void AltClickCyclesOverlappingRepeatedBatchesAndOrdinaryRoi()
    {
        var editor = new EditorSession();
        editor.SetImage(new("image", "image", 1000, 1000), ViewportTransform.Identity);
        var normal = editor.Add(new(new(200, 200), 80, 80, 0));
        var first = editor.AddRepeatedGrid(new(1, 1, 100, new(200, 200)));
        var second = editor.AddRepeatedGrid(new(1, 1, 100, new(200, 200)));
        editor.Select([]);
        var input = new InteractionController(editor);
        var before = editor.State.RepeatedRois;
        var history = editor.HistoryBytes;
        Click();
        Assert.Equal(new RepeatedRoiCell(second, 0), editor.State.SingleRepeatedCell);
        Click();
        Assert.Equal(new RepeatedRoiCell(first, 0), editor.State.SingleRepeatedCell);
        Click();
        Assert.Equal(normal, Assert.Single(editor.State.Selection));
        Assert.Null(editor.State.SingleRepeatedCell);
        Click();
        Assert.Equal(new RepeatedRoiCell(second, 0), editor.State.SingleRepeatedCell);
        Assert.Same(before, editor.State.RepeatedRois);
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
        Assert.False(editor.IsPreviewing);
    }

    [Fact]
    public void DoubleClickReplacesMultipleSelectionAndPreservesRedo()
    {
        var editor = new EditorSession();
        var first = editor.Add(new(new(100, 100), 40, 40, 0));
        var second = editor.Add(new(new(200, 100), 40, 40, 0));
        var target = editor.Add(new(new(300, 100), 40, 40, 0));
        editor.Add(new(new(400, 100), 40, 40, 0));
        editor.Undo();
        editor.Select([first, second]);
        var input = new InteractionController(editor);

        input.DoubleClick(new(300, 100), InputModifiers.Control | InputModifiers.Shift);
        input.Up(new(300, 100));

        Assert.Equal(target, Assert.Single(editor.State.Selection));
        Assert.True(editor.CanRedo);
    }

    [Fact]
    public void DoubleClickUsesImageCoordinatesAndExactShapeHit()
    {
        var editor = new EditorSession();
        var parent = editor.Add(new(new(100, 100), 160, 160, 0));
        var child = editor.AddChild(parent, new(new(100, 100), 30, 60, 45));
        editor.Select([parent]);
        editor.SetViewport(new(2, new(50, 30)));
        var input = new InteractionController(editor);

        input.DoubleClick(editor.State.Viewport.ImageToScreen(new(100, 100)));

        Assert.Equal(child, Assert.Single(editor.State.Selection));
        Assert.Equal(parent, editor.State.Rois[child].ParentId);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void DoubleClickDoesNotChangeSelectionDuringNavigationOrInMinimap(bool navigationOnly, bool minimap)
    {
        var editor = new EditorSession();
        editor.SetImage(new("image", "image", 1000, 1000), ViewportTransform.Identity);
        var selected = editor.Add(new(new(100, 100), 40, 40, 0));
        var input = new InteractionController(editor) { NavigationOnly = navigationOnly };
        input.SetViewportSize(800, 600);
        var point = minimap ? input.Minimap!.ImageBounds.Center : new PointD(300, 100);

        input.DoubleClick(point);
        input.Up(point);

        Assert.Equal(selected, Assert.Single(editor.State.Selection));
    }
}
