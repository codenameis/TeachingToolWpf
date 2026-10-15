using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Geometry;
using VisionCanvas.Core.Interaction;
using VisionGeometry;

namespace TeachingToolWpf.Core.Tests;

public class HandleScaleTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ScaledHandleTargetsResizeAndRotateWithUndoRedo(int kind)
    {
        foreach (var scale in new[] { .5, 1, 3 })
        {
            var (editor, input, geometry) = Create(kind);
            var original = geometry();
            input.HandleScale = scale;
            var corner = HandleGeometry.ResizeHandles(original, editor.State.Viewport)[4];
            // 300%에서는 기존 9 DIP 클릭 범위 밖에서도 새 핸들을 잡을 수 있다.
            var grab = corner + new PointD(6 * scale, 0);
            var target = grab + new PointD(30, 20);
            input.Down(grab);
            input.Move(target);
            input.Up(target);
            var resized = geometry();
            Assert.True(resized.Width > original.Width && resized.Height > original.Height);
            Assert.Equal(scale, input.Overlay.HandleScale);
            editor.Undo();
            Assert.Equal(original, geometry());
            editor.Redo();
            Assert.Equal(resized, geometry());

            var handle = HandleGeometry.RotationHandle(resized, editor.State.Viewport, scale);
            grab = handle + new PointD(6 * scale, 0);
            target = resized.Center + (grab - resized.Center).Rotate(20);
            input.Down(grab);
            input.Move(target);
            input.Up(target);
            Assert.Equal(resized.Angle + 20, geometry().Angle, 6);
            var rotated = geometry();
            editor.Undo();
            Assert.Equal(resized, geometry());
            editor.Redo();
            Assert.Equal(rotated, geometry());
            input.Cancel();
            Assert.Equal(scale, input.Overlay.HandleScale);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void SmallerHandlesDoNotKeepTheOldHitArea(int kind)
    {
        var (editor, input, geometry) = Create(kind);
        input.HandleScale = .5;
        var original = geometry();
        var corner = HandleGeometry.ResizeHandles(original, editor.State.Viewport)[4];
        var grab = corner + new PointD(8, 0);
        input.Down(grab);
        input.Move(grab + new PointD(30, 20));
        input.Up(grab + new PointD(30, 20));
        Assert.Equal(original, geometry());
    }

    [Fact]
    public void SizeChangesPreserveDocumentHistoryAndActiveGesture()
    {
        var (editor, input, geometry) = Create(0);
        Assert.Equal(1, input.HandleScale);
        var original = editor.State;
        var history = editor.HistoryBytes;
        input.HandleScale = 2;
        Assert.Same(original, editor.State);
        Assert.Equal(history, editor.HistoryBytes);
        var corner = HandleGeometry.ResizeHandles(geometry(), editor.State.Viewport)[4];
        input.Down(corner);
        input.Move(corner + new PointD(30, 20));
        var preview = geometry();
        input.HandleScale = 3;
        Assert.True(input.IsDragging);
        Assert.Equal(preview, geometry());
        input.Up(corner + new PointD(30, 20));
        Assert.Equal(preview, geometry());
        editor.Undo();
        Assert.Equal(original.Rois.Single().Value.Geometry, geometry());
        Assert.True(editor.CanRedo);
        input.HandleScale = .5;
        Assert.True(editor.CanRedo);
        editor.Redo();
        Assert.Equal(preview, geometry());
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(0)]
    [InlineData(.49)]
    [InlineData(3.01)]
    public void InvalidScaleDoesNotChangeInput(double scale)
    {
        var (_, input, _) = Create(0);
        Assert.Throws<ArgumentOutOfRangeException>(() => input.HandleScale = scale);
        Assert.Equal(1, input.HandleScale);
        Assert.Equal(1, input.Overlay.HandleScale);
    }

    [Theory]
    [InlineData(.25)]
    [InlineData(2)]
    public void RotationHandleGapUsesScreenUnits(double zoom)
    {
        var geometry = new RoiGeometry(new(500, 500), 200, 140, 35);
        var view = new ViewportTransform(zoom, new(100, 50));
        var top = view.ImageToScreen(geometry.ToWorld(new(0, -geometry.Height / 2)));
        foreach (var scale in new[] { .5, 1, 3 })
        {
            Assert.Equal(28 * scale, (HandleGeometry.RotationHandle(geometry, view, scale) - top).Length, 8);
        }
    }

    private static (EditorSession Editor, InteractionController Input, Func<RoiGeometry> Geometry) Create(int kind)
    {
        var editor = new EditorSession();
        editor.InitializeImage(new("test", "test", 1000, 1000), ViewportTransform.Identity);
        Func<RoiGeometry> geometry;
        if (kind == 2)
        {
            var id = editor.AddRepeatedGrid(new(1, 1, 250, new(500, 500)));
            editor.SelectRepeated(new(id, 0));
            geometry = () => editor.State.RepeatedRois[id].Geometry(0);
        }
        else
        {
            var frame = new RoiGeometry(new(500, 500), 200, 140, 0);
            var id = kind == 1 ? editor.AddParent(frame) : editor.Add(frame);
            if (kind == 1)
            {
                editor.AddChild(id, new(new(500, 500), 20, 20, 0));
                editor.Select([id]);
            }
            geometry = () => editor.State.Rois[id].Geometry;
        }
        return (editor, new(editor), geometry);
    }
}
