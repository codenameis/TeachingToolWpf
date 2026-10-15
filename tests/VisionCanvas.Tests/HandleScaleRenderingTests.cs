using System.Numerics;
using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Geometry;
using VisionCanvas.Core.Interaction;
using VisionCanvas.Rendering;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace VisionCanvas.Tests;

public class HandleScaleRenderingTests
{
    [Theory]
    [InlineData(false, .5)]
    [InlineData(false, 1)]
    [InlineData(false, 3)]
    [InlineData(true, .5)]
    [InlineData(true, 1)]
    [InlineData(true, 3)]
    public void ResizeAndRotationHandlePixelsFollowScale(bool repeated, double scale)
    {
        var editor = new EditorSession();
        editor.InitializeImage(new("test", "test", 1000, 1000), ViewportTransform.Identity);
        RoiGeometry geometry;
        if (repeated)
        {
            var id = editor.AddRepeatedGrid(new(1, 1, 250, new(500, 500)));
            editor.SelectRepeated(new(id, 0));
            geometry = editor.State.RepeatedRois[id].Geometry(0);
        }
        else
        {
            var id = editor.Add(new(new(500, 500), 200, 140, 0));
            geometry = editor.State.Rois[id].Geometry;
        }
        var corner = HandleGeometry.ResizeHandles(geometry, editor.State.Viewport)[4];
        var rotation = HandleGeometry.RotationHandle(geometry, editor.State.Viewport, scale);
        var white = new GpuTexture(null!, 0, PixelKind.Rgba32, 1, 1, _ => { });
        using var filters = new GpuFilterPreview();
        var scene = new RenderScene(editor.State, null, 0, 1000, 1000, 1, new InteractionOverlay { HandleScale = scale });
        var mesh = new ScenePainter().Build(scene, white, new Dictionary<TileKey, GpuTexture>(), _ => white,
            _ => null, null, default, filters, null);
        bool HasWhiteVertex(PointD point) => mesh.Vertices.Any(v => v.Position == new Vector2((float)((point.X / 500) - 1),
            (float)(1 - (point.Y / 500))) && v.Color == Vector4.One);
        Assert.True(HasWhiteVertex(corner - new PointD(3 * scale, 3 * scale)));
        Assert.True(HasWhiteVertex(rotation));
        Assert.True(HasWhiteVertex(rotation + new PointD(3.5 * scale, 0)));
        if (scale != 1)
        {
            Assert.False(HasWhiteVertex(corner - new PointD(3, 3)));
        }
    }
}
