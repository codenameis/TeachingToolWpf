using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Geometry;
using VisionCanvas.Core.Interaction;
using VisionGeometry;

namespace TeachingToolWpf.Core.Tests;

public class MinimapTests
{
    [Theory]
    [InlineData(8192, 8192)]
    [InlineData(24576, 131072)]
    [InlineData(32768, 512)]
    public void MappingPreservesAspectAndVisibleImageBounds(int width, int height)
    {
        var map = MinimapLayout.Create(width, height, 1000, 600)!;
        Assert.Equal(width / (double)height, map.ImageBounds.Width / map.ImageBounds.Height, 8);
        var point = new PointD(width * .37, height * .61);
        Assert.True((point - map.MapToImage(map.ImageToMap(point))).Length < 1e-7);
        var fit = ViewportTransform.Fit(width, height, 1000, 600);
        Assert.Equal(map.ImageBounds, map.VisibleBounds(fit));
        var centered = map.CenterView(new(2, new()), point);
        Assert.True((point - centered.ScreenToImage(new(500, 300))).Length < 1e-7);
    }

    [Fact]
    public void NavigationPreservesRoisSelectionHistoryAndCancelRestoresView()
    {
        var editor = new EditorSession();
        editor.InitializeImage(new("image", "image", 8192, 8192), new(1, new(-2000, -2000)));
        var id = editor.Add(new(new(3000, 3000), 50, 50, 0));
        editor.Rename(id, "부품");
        editor.Undo();
        var before = editor.State;
        var input = new InteractionController(editor) { Tool = EditorTool.Draw };
        input.SetViewportSize(1000, 600);
        var map = input.Minimap!;
        var target = map.ImageToMap(new(5000, 4000));
        input.Down(target);
        input.Up(target);
        Assert.True((editor.State.Viewport.ScreenToImage(new(500, 300)) - new PointD(5000, 4000)).Length < 1e-7);
        Assert.Same(before.Rois, editor.State.Rois);
        Assert.Same(before.Selection, editor.State.Selection);
        Assert.True(editor.CanRedo);
        Assert.Equal(EditorTool.Draw, input.Tool);
        var view = editor.State.Viewport;
        var grab = map.VisibleBounds(view).Center;
        input.Down(grab);
        input.Move(grab + new PointD(10, 5));
        input.Cancel();
        Assert.Equal(view, editor.State.Viewport);
        input.Down(new(map.Panel.Left + 2, map.Panel.Top + 2));
        input.Up(new(map.Panel.Left + 50, map.Panel.Top + 5));
        Assert.Equal(view, editor.State.Viewport);
        Assert.Single(editor.State.Rois);
    }

    [Fact]
    public void DragClampsToImageAndWindowResizeCancelsCapture()
    {
        var editor = new EditorSession();
        editor.InitializeImage(new("image", "image", 8192, 8192), ViewportTransform.Identity);
        var input = new InteractionController(editor);
        input.SetViewportSize(1000, 600);
        var map = input.Minimap!;
        input.Down(map.ImageBounds.Center);
        input.Move(new(-100, -100));
        input.Up(new(-100, -100));
        Assert.Equal(new PointD(0, 0), editor.State.Viewport.ScreenToImage(new(0, 0)));
        var view = editor.State.Viewport;
        input.Down(map.ImageBounds.Center);
        input.Move(map.ImageBounds.Center + new PointD(10, 5));
        input.SetViewportSize(800, 500);
        Assert.False(input.IsDragging);
        Assert.Equal(view, editor.State.Viewport);
        input.SetViewportSize(0, 0);
        Assert.Null(input.Minimap);
    }

    [Fact]
    public void ClickInsideVisibleRectangleCentersClickWhileDragKeepsGrabOffset()
    {
        var editor = new EditorSession();
        editor.InitializeImage(new("image", "image", 8192, 8192), new(1, new(-2000, -2000)));
        var input = new InteractionController(editor);
        input.SetViewportSize(1000, 600);
        var map = input.Minimap!;
        var target = new PointD(2600, 2350);
        var point = map.ImageToMap(target);
        input.Down(point);
        input.Up(point);
        Assert.True((target - editor.State.Viewport.ScreenToImage(new(500, 300))).Length < 1e-7);
        var offCenter = map.ImageToMap(target + new PointD(150, 50));
        input.Down(offCenter);
        input.Move(offCenter + new PointD(5, 5));
        input.Up(offCenter + new PointD(5, 5));
        var expected = target + map.MapToImage(offCenter + new PointD(5, 5)) - map.MapToImage(offCenter);
        Assert.True((expected - editor.State.Viewport.ScreenToImage(new(500, 300))).Length < 1e-7);
    }
}
