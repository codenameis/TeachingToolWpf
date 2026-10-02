using System.Runtime.InteropServices;
using TeachingToolWpf.Core.Editing;
using TeachingToolWpf.Core.Geometry;
using TeachingToolWpf.Core.Imaging;
using TeachingToolWpf.Core.Interaction;
using TeachingToolWpf.Rendering;

namespace TeachingToolWpf.Imaging.Tests;

public class RoiInstanceTests
{
    [Fact] public void LabelsFollowEnteringAndLeavingTranslationCandidatesAndCancel()
    {
        var editor = new EditorSession(); editor.InitializeImage(new("image", "image", 2000, 2000), ViewportTransform.Identity);
        var inside = editor.Add(new(new(200, 200), 40, 40, 0)); editor.Rename(inside, "inside");
        var outside = editor.Add(new(new(1000, 200), 40, 40, 0)); editor.Rename(outside, "outside");
        editor.Select([inside, outside]);
        var painter = new RoiLabelPainter(); var mesh = new SpriteMesh();
        var texture = new GpuTexture(null!, 0, PixelKind.Rgba32, 40, 12, _ => { });
        List<string> Draw()
        {
            var text = new List<string>(); GpuTexture Rasterize(string value) { text.Add(value); return texture; }
            mesh.Begin(400, 400);
            painter.Draw(mesh, new(editor.State, null, 0, 400, 400, 1, new InteractionOverlay(), Translation: editor.TranslationPreview), texture, Rasterize, Rasterize);
            return text;
        }
        Assert.Contains(Draw(), s => s.StartsWith("inside"));
        // Keep both ROIs within the image while moving the viewport for an off-screen-entry case.
        editor.SetViewport(editor.State.Viewport.Pan(new(-700, 0)));
        Assert.Contains(Draw(), s => s.StartsWith("outside"));
        editor.BeginPreview(); editor.PreviewTranslation(new(700, 0));
        var moving = Draw(); Assert.Contains(moving, s => s.StartsWith("inside")); Assert.DoesNotContain(moving, s => s.StartsWith("outside"));
        editor.CancelPreview(); var canceled = Draw();
        Assert.Contains(canceled, s => s.StartsWith("outside")); Assert.DoesNotContain(canceled, s => s.StartsWith("inside"));
    }
    [Fact] public void EdgeLabelRemainsClampedDuringCachedPan()
    {
        var outside = new PointD(-100, -80);
        Assert.Equal(new PointD(4, 4), RoiLabelPainter.ClampAnchor(outside, 1000, 600));
        Assert.Equal(new PointD(4, 4), RoiLabelPainter.ClampAnchor(outside + new PointD(20, -40), 1000, 600));
        Assert.Equal(new PointD(50, 30), RoiLabelPainter.ClampAnchor(new(50, 30), 1000, 600));
    }
    [Fact] public void InstancesPreserveRotatedCornersAtLargeImageCoordinates()
    {
        var shapes = new[] { new RotatedRect(new(1e12 + 20, 1e12 + 10), 50, 25, 12), new RotatedRect(new(1e12 + 120, 1e12 + 80), 10, 75, 275) };
        var rois = shapes.Select((s, i) => new Roi(Guid.NewGuid(), "ROI", s, null, false, i)).ToArray();
        var index = new RoiSpatialIndex(rois); var block = index.Blocks(index.Bounds).Single();
        var packed = RoiInstance.Pack(block, 0);
        Assert.Equal(32, Marshal.SizeOf<RoiInstance>());
        for (var i = 0; i < shapes.Length; i++)
        {
            var instance = packed[i];
            var signs = new[] { (-1, -1), (1, -1), (1, 1), (-1, 1) };
            for (var j = 0; j < 4; j++)
            {
                var relative = instance.Center + instance.AxisX * signs[j].Item1 + instance.AxisY * signs[j].Item2;
                var point = block.Bounds.Center + new PointD(relative.X, relative.Y);
                Assert.InRange((point - shapes[i].Corners[j]).Length, 0, .001);
            }
        }
    }
    [Fact] public void IdenticalVisibleShapesShareGeometryWhileDomainIdsStayDistinct()
    {
        var shape = new RotatedRect(new(100, 100), 50, 40, 12);
        var rois = Enumerable.Range(0, 1000).Select(i => new Roi(Guid.NewGuid(), "ROI", shape, null, false, i)).ToArray();
        var index = new RoiSpatialIndex(rois); var block = index.Blocks(index.Bounds).Single();
        Assert.Single(RoiInstance.Pack(block, 0));
        Assert.Equal(1000, block.Detail(0).Select(r => r.Roi!.Id).Distinct().Count());
        var summary = RoiInstance.Pack(block, 100).Single();
        Assert.Equal(1u, summary.Summary);
    }
}
