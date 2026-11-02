using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Interaction;
using VisionGeometry;

namespace TeachingToolWpf.Core.Tests;

public sealed class InspectionInteractionTests
{
    [Theory]
    [InlineData(EditorTool.Select)]
    [InlineData(EditorTool.Pan)]
    public void SelectedRoiCanBeNudgedWhileIdleInSelectionOrPanTool(EditorTool tool)
    {
        var editor = CreateEditor();
        var id = editor.Add(new(new(100, 100), 50, 50, 0));
        var input = new InteractionController(editor) { Tool = tool };
        Assert.True(input.NudgeSelection(new(1, 0)));
        Assert.Equal(new PointD(101, 100), editor.State.Rois[id].Geometry.Center);
    }

    [Fact]
    public void LockCancelsPreviewAndRejectsAllPointerNavigationAndNudges()
    {
        var editor = CreateEditor();
        editor.Add(new(new(100, 100), 50, 50, 0));
        var input = new InteractionController(editor);
        input.SetViewportSize(800, 600);
        var before = editor.State;
        input.Down(new(100, 100));
        input.Move(new(120, 100));
        input.IsEnabled = false;
        Assert.False(input.IsDragging);
        Assert.Equal(before, editor.State);
        input.Down(new(10, 10), middle: true);
        input.Move(new(200, 200));
        input.Up(new(200, 200));
        input.DoubleClick(new(100, 100));
        input.Wheel(new(100, 100), 2);
        Assert.False(input.NudgeSelection(new(1, 0)));
        Assert.Equal(before, editor.State);
        input.IsEnabled = true;
        Assert.True(input.NudgeSelection(new(1, 0)));
        Assert.Equal(new PointD(101, 100), editor.State.Rois.Values.Single().Geometry.Center);
        editor.Undo();
        Assert.Equal(before.Rois, editor.State.Rois);
        editor.Redo();
        Assert.Equal(new PointD(101, 100), editor.State.Rois.Values.Single().Geometry.Center);
    }

    [Fact]
    public void NudgeKeepsParentBoundaryAndDoesNotDoubleMoveSelectedChild()
    {
        var editor = CreateEditor();
        var parent = editor.AddParent(new(new(100, 100), 100, 100, 0));
        var child = editor.AddChild(parent, new(new(135, 100), 20, 20, 0));
        var input = new InteractionController(editor);
        input.NudgeSelection(new(10, 0));
        // 기존 포함 판정의 1e-6px 허용 오차를 포함한다.
        Assert.InRange(editor.State.Rois[child].Geometry.Center.X, 140, 140.0000011);
        var childBefore = editor.State.Rois[child].Geometry.Center;
        editor.Select([parent, child]);
        input.NudgeSelection(new(1, 0));
        Assert.Equal(101, editor.State.Rois[parent].Geometry.Center.X, 6);
        Assert.Equal(childBefore + new PointD(1, 0), editor.State.Rois[child].Geometry.Center);
    }

    [Fact]
    public void FocusFitsRotatedRoiWithoutSelectingOrChangingHistory()
    {
        var editor = CreateEditor();
        var target = editor.Add(new(new(700, 600), 180, 70, 37));
        editor.Add(new(new(100, 100), 40, 40, 0));
        var before = editor.State;
        var history = editor.HistoryBytes;
        Assert.True(editor.FocusRoi(target, 800, 600));
        Assert.Same(before.Selection, editor.State.Selection);
        Assert.Same(before.Rois, editor.State.Rois);
        Assert.Equal(history, editor.HistoryBytes);
        Assert.True((new PointD(400, 300) - editor.State.Viewport.ImageToScreen(new(700, 600))).Length < 1e-9);
        foreach (var corner in editor.State.Rois[target].Geometry.FrameCorners)
        {
            var screen = editor.State.Viewport.ImageToScreen(corner);
            Assert.InRange(screen.X, 0, 800);
            Assert.InRange(screen.Y, 0, 600);
        }
        var focused = editor.State;
        Assert.False(editor.FocusRoi(Guid.NewGuid(), 800, 600));
        Assert.False(editor.FocusRoi(target, 0, 600));
        Assert.Equal(focused, editor.State);
        Assert.True(editor.FitImage(800, 600));
        Assert.Equal(ViewportTransform.Fit(1000, 1000, 800, 600), editor.State.Viewport);
    }

    private static EditorSession CreateEditor()
    {
        var editor = new EditorSession();
        editor.InitializeImage(new("test", "test", 1000, 1000), ViewportTransform.Identity);
        return editor;
    }
}
