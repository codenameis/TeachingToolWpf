using System.Numerics;
using System.Runtime.InteropServices;
using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Geometry;
using VisionCanvas.Core.Interaction;
using VisionCanvas.Rendering;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Imaging.Tests;

public class RoiInstanceTests
{
    [Fact]
    public void HiddenParentNameStaysHiddenWhenSelectedAndUndoRestoresLabel()
    {
        var editor = new EditorSession();
        var parent = editor.Add(new(new(200, 200), 80, 80, 0));
        editor.AddChild(parent, new(new(200, 200), 20, 20, 0));
        editor.Select([parent]);
        var painter = new RoiLabelPainter();
        var mesh = new SpriteMesh();
        var texture = new GpuTexture(null!, 0, PixelKind.Rgba32, 40, 12, _ => { });
        var labels = new List<string>();
        GpuTexture Rasterize(string text)
        {
            labels.Add(text);
            return texture;
        }
        void Draw()
        {
            labels.Clear();
            mesh.Begin(500, 500);
            painter.Draw(mesh, new(editor.State, null, 0, 500, 500, 1, new InteractionOverlay()), texture, Rasterize, Rasterize);
        }
        editor.SetProperties(parent, "한글 그룹", editor.State.Rois[parent].Geometry, null, false);
        Draw();
        Assert.Empty(labels);
        editor.Undo();
        Draw();
        Assert.Single(labels);
        editor.Redo();
        Draw();
        Assert.Empty(labels);
    }

    [Fact]
    public void SearchDrawUsesRedDashedPassWithoutChangingGpuLayout()
    {
        var scene = new RenderScene(DocumentState.Empty, null, 0, 500, 500, 1.5, new InteractionOverlay());
        var constants = new RoiDrawConstants(scene, default, default, false, RoiDrawPass.SearchRange);
        Assert.Equal(128, Marshal.SizeOf<RoiDrawConstants>());
        Assert.Equal(1, constants.SearchRangePass);
        Assert.Equal(new Vector4(1, 0, 0, 1), constants.DefaultColor);
        Assert.Equal(constants.DefaultColor, constants.ParentColor);
        Assert.Equal(new Vector4(1.5f, 1.5f, 1.5f, 0), constants.StrokeWidths);
    }

    [Fact]
    public void LabelsFollowEnteringAndLeavingTranslationCandidatesAndCancel()
    {
        var editor = new EditorSession();
        editor.InitializeImage(new("image", "image", 2000, 2000), ViewportTransform.Identity);
        var inside = editor.Add(new(new(200, 200), 40, 40, 0));
        editor.Rename(inside, "inside");
        var outside = editor.Add(new(new(1000, 200), 40, 40, 0));
        editor.Rename(outside, "outside");
        editor.Select([inside, outside]);
        var painter = new RoiLabelPainter();
        var mesh = new SpriteMesh();
        var texture = new GpuTexture(null!, 0, PixelKind.Rgba32, 40, 12, _ =>
        {
        });
        List<string> Draw()
        {
            var text = new List<string>();
            GpuTexture Rasterize(string value)
            {
                text.Add(value);
                return texture;
            }
            mesh.Begin(400, 400);
            painter.Draw(mesh, new(editor.State, null, 0, 400, 400, 1, new InteractionOverlay(), Transform: editor.TransformPreview), texture, Rasterize, Rasterize);
            return text;
        }
        Assert.Contains(Draw(), s => s.StartsWith("inside"));
        // Keep both ROIs within the image while moving the viewport for an off-screen-entry case.
        editor.SetViewport(editor.State.Viewport.Pan(new(-700, 0)));
        Assert.Contains(Draw(), s => s.StartsWith("outside"));
        editor.BeginPreview();
        editor.PreviewTranslation(new(700, 0));
        var moving = Draw();
        Assert.Contains(moving, s => s.StartsWith("inside"));
        Assert.DoesNotContain(moving, s => s.StartsWith("outside"));
        editor.CancelPreview();
        var canceled = Draw();
        Assert.Contains(canceled, s => s.StartsWith("outside"));
        Assert.DoesNotContain(canceled, s => s.StartsWith("inside"));
    }

    [Fact]
    public void EdgeLabelRemainsClampedDuringCachedPan()
    {
        var outside = new PointD(-100, -80);
        Assert.Equal(new PointD(4, 4), RoiLabelPainter.ClampAnchor(outside, 1000, 600));
        Assert.Equal(new PointD(4, 4), RoiLabelPainter.ClampAnchor(outside + new PointD(20, -40), 1000, 600));
        Assert.Equal(new PointD(50, 30), RoiLabelPainter.ClampAnchor(new(50, 30), 1000, 600));
    }

    [Fact]
    public void InstancesPreserveRotatedCornersAtLargeImageCoordinates()
    {
        var shapes = new[] { new RoiGeometry(new(1e12 + 20, 1e12 + 10), 50, 25, 12), new RoiGeometry(new(1e12 + 120, 1e12 + 80), 10, 75, 275) };
        var rois = shapes.Select((s, i) => new Roi(Guid.NewGuid(), "ROI", s, null, false, i)).ToArray();
        var index = new RoiSpatialIndex(rois);
        var block = index.Blocks(index.Bounds).Single();
        var packed = RoiInstance.Pack(block, 0);
        Assert.Equal(32, Marshal.SizeOf<RoiInstance>());
        for (var i = 0; i < shapes.Length; i++)
        {
            var instance = packed[i];
            var signs = new[] { (-1, -1), (1, -1), (1, 1), (-1, 1) };
            for (var j = 0; j < 4; j++)
            {
                var relative = instance.Center + (instance.AxisX * signs[j].Item1) + (instance.AxisY * signs[j].Item2);
                var point = block.Bounds.Center + new PointD(relative.X, relative.Y);
                Assert.InRange((point - shapes[i].FrameCorners[j]).Length, 0, .001);
            }
        }
    }

    [Fact]
    public void IdenticalVisibleShapesShareGeometryWhileDomainIdsStayDistinct()
    {
        var shape = new RoiGeometry(new(100, 100), 50, 40, 12);
        var rois = Enumerable.Range(0, 1000).Select(i => new Roi(Guid.NewGuid(), "ROI", shape, null, false, i)).ToArray();
        var index = new RoiSpatialIndex(rois);
        var block = index.Blocks(index.Bounds).Single();
        Assert.Single(RoiInstance.Pack(block, 0));
        Assert.Equal(1000, block.Detail(0).Select(r => r.Roi!.Id).Distinct().Count());
        var summary = RoiInstance.Pack(block, 100).Single();
        Assert.Equal(1u, summary.Summary);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(45)]
    [InlineData(90)]
    [InlineData(-90)]
    [InlineData(180)]
    public void ShaderRelativeRotationMatchesDomainCornersAtLargeCoordinates(double angle)
    {
        foreach (var extent in new[] { 0d, 24576d, 1e12 })
        {
            var pivot = new PointD(extent + 300, extent + 300);
            var shape = new RoiGeometry(new(extent + 600, extent + 210), 123, 47, 27);
            var other = new RoiGeometry(new(extent + 420, extent + 420), 55, 33, 315);
            var index = new RoiSpatialIndex(new[] { shape, other }.Select((geometry, i) => new Roi(Guid.NewGuid(), "ROI", geometry, null, false, i)));
            var block = index.Blocks(index.Bounds).Single();
            var packed = RoiInstance.Pack(block, 0);
            var transform = new RigidTransform2D(pivot, angle, new(17, -13));
            var view = new ViewportTransform(.35, new PointD(300, 200) - (transform.Apply(pivot) * .35));
            var screenOrigin = view.ImageToScreen(transform.Apply(block.Bounds.Center));
            var origin = new Vector2((float)screenOrigin.X, (float)screenOrigin.Y);
            var cosine = (float)transform.Cosine;
            var sine = (float)transform.Sine;
            var expected = shape.RotateAround(pivot, angle).Translate(new(17, -13)).FrameCorners;
            var signs = new[] { (-1, -1), (1, -1), (1, 1), (-1, 1) };
            for (var i = 0; i < signs.Length; i++)
            {
                // Same float operations and block-relative inputs as Roi.hlsl; the oracle uses domain double corners.
                var local = packed[0].Center + (packed[0].AxisX * signs[i].Item1) + (packed[0].AxisY * signs[i].Item2);
                var shader = (new Vector2((local.X * cosine) - (local.Y * sine), (local.X * sine) + (local.Y * cosine)) * (float)view.Scale) + origin;
                Assert.InRange((new PointD(shader.X, shader.Y) - view.ImageToScreen(expected[i])).Length, 0, .003);
            }
        }
    }

    [Theory]
    [InlineData(45)]
    [InlineData(90)]
    [InlineData(-90)]
    [InlineData(180)]
    public void InverseViewportQueryFindsRoisRotatedIntoView(double angle)
    {
        var transform = new RigidTransform2D(new(1200, 900), angle, new(27, -31));
        var sourceCenter = transform.InverseApply(new(200, 150));
        var outsideCenter = transform.InverseApply(new(1200, 900));
        var inside = new Roi(Guid.NewGuid(), "enter", new(sourceCenter, 40, 20, 17), null, false, 0);
        var outside = new Roi(Guid.NewGuid(), "outside", new(outsideCenter, 40, 20, 17), null, false, 1);
        var index = new RoiSpatialIndex([inside, outside]);
        var candidates = new RoiVisibility();
        candidates.Update(index, ViewportTransform.Identity, 400, 300, transform);
        Assert.Contains(candidates.Rois, roi => roi.Id == inside.Id);
        var visible = new BoundsD(-8, -8, 408, 308);
        var blocks = index.Blocks(transform.InverseTransformBounds(visible));
        Assert.Contains(blocks.SelectMany(block => block.Detail(0)), item => item.Roi?.Id == inside.Id);
        var transformed = inside.Geometry.RotateAround(transform.Pivot, angle).Translate(transform.Translation);
        Assert.True(RoiLabelPainter.IsVisible(transformed.Bounds, ViewportTransform.Identity, 400, 300));
    }

    [Fact]
    public void RotationChangesVisibleLabelsAndAngleWithoutMutatingTheRetainedDocument()
    {
        var editor = new EditorSession();
        editor.InitializeImage(new("image", "image", 2000, 2000), ViewportTransform.Identity);
        var inside = editor.Add(new(new(200, 200), 40, 40, 0));
        editor.Rename(inside, "inside");
        var outside = editor.Add(new(new(1000, 200), 40, 40, 0));
        editor.Rename(outside, "outside");
        editor.Select([inside, outside]);
        var state = editor.State;
        var selection = new RoiTransformSelection(state);
        var painter = new RoiLabelPainter();
        var mesh = new SpriteMesh();
        var texture = new GpuTexture(null!, 0, PixelKind.Rgba32, 40, 12, _ =>
        {
        });
        List<string> Draw(RoiTransformPreview? preview)
        {
            var labels = new List<string>();
            GpuTexture Rasterize(string text)
            {
                labels.Add(text);
                return texture;
            }
            mesh.Begin(400, 400);
            painter.Draw(mesh, new(state, null, 0, 400, 400, 1, new InteractionOverlay(), Transform: preview), texture, Rasterize, Rasterize);
            return labels;
        }
        Assert.Contains(Draw(new(selection, RigidTransform2D.Identity)), text => text.StartsWith("inside"));
        var rotated = Draw(new(selection, new(new(600, 200), 180)));
        Assert.Contains(rotated, text => text.StartsWith("outside") && text.EndsWith($"{180:0.0}°"));
        Assert.DoesNotContain(rotated, text => text.StartsWith("inside"));
        var canceled = Draw(null);
        Assert.Contains(canceled, text => text.StartsWith("inside"));
        Assert.DoesNotContain(canceled, text => text.StartsWith("outside"));
        Assert.Same(state, editor.State);
    }

    [Fact]
    public void GroupSelectionCornersUseTheSameRigidTransformAsItsRois()
    {
        var bounds = new BoundsD(100, 200, 300, 400);
        var view = new ViewportTransform(2, new(30, -50));
        var transform = new RigidTransform2D(bounds.Center, 90, new(11, 23));
        var corners = ScenePainter.SelectionCorners(bounds, view, transform);
        var expected = new RoiGeometry(bounds.Center, bounds.Width, bounds.Height, 0).RotateAround(bounds.Center, 90).Translate(new(11, 23)).FrameCorners;
        for (var i = 0; i < 4; i++)
        {
            Assert.InRange((corners[i] - view.ImageToScreen(expected[i])).Length, 0, 1e-9);
        }

        var handle = ((corners[0] + corners[1]) / 2) + transform.ApplyVector(new(0, -28));
        var rotationHandle = HandleGeometry.RotationHandle(new(bounds.Center + transform.Translation, bounds.Width, bounds.Height, 90), view);
        Assert.InRange((handle - rotationHandle).Length, 0, 1e-9);
    }

    [Fact]
    public void LargeOverlappingRoisBoundLabelCandidatesWithoutChangingExactGeometryQueries()
    {
        var shape = new RoiGeometry(new(200, 200), 300, 300, 0);
        var rois = Enumerable.Range(0, 12000).Select(i => new Roi(Guid.NewGuid(), $"overlap {i}", shape, null, false, i)).ToArray();
        var index = new RoiSpatialIndex(rois);
        var visibility = new RoiVisibility();
        visibility.Update(index, ViewportTransform.Identity, 400, 400);
        Assert.Equal(RoiVisibility.CandidateBudget, visibility.Rois.Count);
        Assert.Empty(visibility.Summaries);
        var ids = visibility.Rois.Select(roi => roi.Id).ToArray();
        Assert.Equal(visibility.Rois.OrderBy(roi => roi.Order).Select(roi => roi.Id), ids);
        visibility.Update(index, ViewportTransform.Identity, 400, 400, new(new(200, 200), 45));
        Assert.Equal(ids, visibility.Rois.Select(roi => roi.Id));
        visibility.Update(index, ViewportTransform.Identity, 400, 400, new(new(200, 200), 90));
        Assert.Equal(ids, visibility.Rois.Select(roi => roi.Id));
        // The decoration budget must not cap the geometry path or make unlabelled ROI unselectable.
        Assert.Equal(rois.Length, index.At(shape.Center).Count());
        Assert.Equal(rois[^1].Id, index.HitTest(shape.Center, 0)!.Id);
        Assert.Equal(rois.Length, index.Blocks(index.Bounds).SelectMany(block => block.Detail(0)).Count());
    }
}
