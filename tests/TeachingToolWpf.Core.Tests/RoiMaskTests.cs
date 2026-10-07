using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Interaction;
using VisionGeometry;

namespace TeachingToolWpf.Core.Tests;

public sealed class RoiMaskTests
{
    [Fact]
    public void LastAllowedMaskFinishesDrawingAndCannotStartAnother()
    {
        var editor = Create();
        var id = editor.Add(new(new(200, 200), 100, 100, 0));
        var mask = new MaskRegion(MaskShape.Rectangle, new(.1, .1, .2, .2));
        for (var index = 0; index < EditorSession.MaximumMasksPerRoi - 1; index++)
        {
            editor.AddMask(id, mask);
        }
        var input = new InteractionController(editor);
        input.StartMask(MaskShape.Rectangle);
        input.Down(new(180, 180));
        input.Up(new(200, 200));
        Assert.Equal(EditorSession.MaximumMasksPerRoi, editor.State.Rois[id].Masks.Length);
        Assert.Equal(EditorTool.Select, input.Tool);
        input.StartMask(MaskShape.Ellipse);
        Assert.Equal(EditorTool.Select, input.Tool);
        editor.Undo();
        input.StartMask(MaskShape.Ellipse);
        Assert.Equal(EditorTool.Mask, input.Tool);
    }

    [Theory]
    [InlineData(MaskShape.Rectangle)]
    [InlineData(MaskShape.Ellipse)]
    public void MaskFollowsRotationResizeAndActualOwnerShape(MaskShape shape)
    {
        var mask = new MaskRegion(shape, new(.1, .1, .9, .9));
        foreach (var angle in new[] { 0d, 37, 180, 315 })
        {
            var owner = new RoiGeometry(new(120, 180), 80, 80, angle, RoiShape.Circle);
            Assert.True(mask.Contains(owner, owner.Center));
            Assert.False(mask.Contains(owner, owner.ToWorld(new(-39, -39))));
            var resized = owner.WithFrame(new(400, 500), 160, 160, angle);
            Assert.True(mask.Contains(resized, resized.ToWorld(new(20, 0))));
            Assert.False(mask.Contains(resized, resized.ToWorld(new(79, 0))));
        }
        var concave = RoiGeometry.Polygon([new(0, 0), new(100, 0), new(100, 100), new(60, 100), new(60, 40), new(40, 40), new(40, 100), new(0, 100)]);
        Assert.False(mask.Contains(concave, new(50, 70)));
        Assert.True(mask.Contains(concave, new(50, 25)));
    }

    [Fact]
    public void HistoryCopyDeleteAndNoOpPreserveImmutableMasks()
    {
        var editor = Create();
        var id = editor.Add(new(new(150, 150), 100, 100, 0));
        var region = new MaskRegion(MaskShape.Rectangle, new(.2, .2, .5, .6));
        editor.AddMask(id, region);
        var masked = editor.State.Rois[id];
        var bytes = editor.HistoryBytes;
        editor.ReplaceMask(id, 0, region);
        Assert.Equal(bytes, editor.HistoryBytes);
        editor.Undo();
        Assert.Empty(editor.State.Rois[id].Masks);
        editor.ClearMasks(id);
        Assert.True(editor.CanRedo);
        editor.Redo();
        Assert.Equal(masked, editor.State.Rois[id]);
        var clipboard = new RoiClipboard();
        clipboard.Copy(editor.State);
        editor.Paste(clipboard);
        var copy = editor.State.Rois[editor.State.Selection.Single()];
        Assert.NotEqual(id, copy.Id);
        Assert.Equal(region, Assert.Single(copy.Masks));
        editor.RemoveMask(copy.Id, 0);
        Assert.Single(editor.State.Rois[id].Masks);
        editor.Undo();
        editor.DeleteSelection();
        editor.Undo();
        Assert.Equal(copy, editor.State.Rois[copy.Id]);
    }

    [Fact]
    public void PointerDrawingClipsInRotatedLocalAxesAndCommitsOnce()
    {
        var editor = Create();
        var geometry = new RoiGeometry(new(200, 200), 100, 80, 37);
        var id = editor.Add(geometry);
        var input = new InteractionController(editor);
        input.StartMask(MaskShape.Rectangle);
        var start = geometry.ToWorld(new(-20, -10));
        var end = geometry.ToWorld(new(100, 100));
        input.Down(start);
        input.Move(end);
        Assert.Empty(editor.State.Rois[id].Masks);
        Assert.NotNull(input.Overlay.MaskDraft);
        input.Up(end);
        var mask = Assert.Single(editor.State.Rois[id].Masks);
        Assert.Equal(.3, mask.Bounds.Left, 9);
        Assert.Equal(.375, mask.Bounds.Top, 9);
        Assert.Equal(1, mask.Bounds.Right);
        Assert.Equal(1, mask.Bounds.Bottom);
        Assert.Equal(EditorTool.Mask, input.Tool);
        editor.Undo();
        Assert.Empty(editor.State.Rois[id].Masks);
        Assert.True(editor.CanRedo);
    }

    [Fact]
    public void CancellationOutsideStartAndStaleTargetNeverCommit()
    {
        var editor = Create();
        var id = editor.Add(new(new(200, 200), 100, 100, 0));
        var input = new InteractionController(editor);
        input.StartMask(MaskShape.Ellipse);
        var bytes = editor.HistoryBytes;
        input.Down(new(170, 170));
        input.Move(new(220, 220));
        input.Cancel();
        input.Up(new(220, 220));
        Assert.Equal(bytes, editor.HistoryBytes);
        input.Down(new(20, 20));
        input.Up(new(220, 220));
        Assert.Empty(editor.State.Rois[id].Masks);
        input.Down(new(170, 170));
        editor.Select([]);
        input.Up(new(220, 220));
        Assert.Empty(editor.State.Rois[id].Masks);
        input.ValidateDraftImage();
        Assert.Equal(EditorTool.Select, input.Tool);
    }

    [Fact]
    public void InvalidAndGroupEditsPreserveHistory()
    {
        Assert.Throws<ArgumentException>(() => new MaskRegion(MaskShape.Rectangle, new(0, 0, double.NaN, 1)));
        Assert.Throws<ArgumentException>(() => new MaskRegion(MaskShape.Rectangle, new(0, 0, 1.01, 1)));
        var editor = Create();
        var id = editor.Add(new(new(200, 200), 100, 100, 0));
        editor.AddChild(id, new(new(200, 200), 20, 20, 0));
        var bytes = editor.HistoryBytes;
        Assert.Throws<InvalidOperationException>(() => editor.AddMask(id, new(MaskShape.Rectangle, new(0, 0, 1, 1))));
        Assert.Equal(bytes, editor.HistoryBytes);
    }

    private static EditorSession Create()
    {
        var editor = new EditorSession();
        editor.InitializeImage(new("fixture", "fixture", 1024, 1024), ViewportTransform.Identity);
        return editor;
    }
}
