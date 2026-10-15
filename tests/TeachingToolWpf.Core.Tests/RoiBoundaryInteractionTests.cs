using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Geometry;
using VisionCanvas.Core.Interaction;
using VisionGeometry;

namespace TeachingToolWpf.Core.Tests;

public class RoiBoundaryInteractionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DragCanGoAroundAConcaveGapWithoutSnappingBackAtCommit(bool prepared)
    {
        var editor = new EditorSession();
        var parent = editor.AddParent(RoiGeometry.Polygon([new(0, 0), new(400, 0), new(400, 400),
            new(260, 400), new(260, 100), new(140, 100), new(140, 400), new(0, 400)]));
        var child = editor.AddChild(parent, new(new(70, 250), 40, 40, 0));
        editor.BeginPreview();
        foreach (var position in new PointD[] { new(70, 50), new(330, 50), new(330, 250) })
        {
            editor.PreviewTranslation(position - new PointD(70, 250));
            Assert.Equal(position, editor.TransformPreview!.Geometry(editor.State.Rois[child]).Center);
        }
        if (prepared)
        {
            editor.CommitPrepared(await editor.PreparePreviewAsync("move around gap"));
        }
        else
        {
            editor.CommitPreview("move around gap");
        }
        Assert.Equal(new PointD(330, 250), editor.State.Rois[child].Geometry.Center);
        Assert.True(editor.State.Rois[parent].Geometry.Covers(editor.State.Rois[child].Geometry));
        editor.Undo();
        Assert.Equal(new PointD(70, 250), editor.State.Rois[child].Geometry.Center);
    }

    [Theory]
    [InlineData(RoiShapeKind.Circle, false)]
    [InlineData(RoiShapeKind.Circle, true)]
    [InlineData(RoiShapeKind.Triangle, false)]
    [InlineData(RoiShapeKind.Triangle, true)]
    [InlineData(RoiShapeKind.Polygon, false)]
    [InlineData(RoiShapeKind.Polygon, true)]
    public void RotationLimitPreservesEveryIntermediatePose(RoiShapeKind shape, bool circle)
    {
        var parent = shape == RoiShapeKind.Polygon
            ? RoiGeometry.Polygon([new(0, 0), new(400, 0), new(400, 150), new(150, 150), new(150, 400), new(0, 400)])
            : RoiGeometry.Create(shape, new(200, 200), 400, 400);
        var center = shape == RoiShapeKind.Polygon ? new PointD(80, 80) : new PointD(200, 200);
        var child = new RoiGeometry(center, 40, 40, 17, circle ? RoiShape.Circle : RoiShape.Rectangle);
        var pivot = center + new PointD(50, 80);
        foreach (var requested in new[] { -540d, -180, -45, 45, 180, 540 })
        {
            var angle = RoiMotionBounds.RotationLimit(parent, child, pivot, requested);
            for (var sample = 0; sample <= 200; sample++)
            {
                Assert.True(parent.Covers(child.RotateAround(pivot, angle * sample / 200)), $"{shape}, circle={circle}, angle={angle}, sample={sample}");
            }
        }
    }

    [Fact]
    public void AutomaticPlacementFitsAConcaveParentWhoseFrameCenterIsOutside()
    {
        var parent = RoiGeometry.Polygon([new(0, 0), new(400, 0), new(400, 80), new(80, 80), new(80, 400), new(0, 400)]);
        Assert.False(parent.Contains(parent.Center));
        foreach (var shape in new[] { RoiShapeKind.Rectangle, RoiShapeKind.Circle, RoiShapeKind.Triangle })
        {
            Assert.True(parent.Covers(RoiPlacement.InParent(parent, shape)));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LeafCannotLeaveItsParentThroughMoveRotateOrResize(bool prepared)
    {
        var editor = new EditorSession();
        var parent = editor.AddParent(new(new(500, 500), 300, 100, 30));
        var child = editor.AddChild(parent, new(new(500, 500), 250, 60, 30));
        var before = editor.State;
        if (prepared)
        {
            editor.CommitPrepared(await editor.PrepareTranslationAsync(new(500, 0)));
        }
        else
        {
            editor.TranslateSelection(new(500, 0));
        }
        AssertContained();
        editor.Undo();
        Assert.Equal(before.Rois, editor.State.Rois);
        editor.BeginPreview();
        editor.PreviewRotation(new(500, 500), 90);
        Assert.True(editor.State.Rois[parent].Geometry.Covers(editor.TransformPreview!.Geometry(editor.State.Rois[child])));
        editor.CommitPreview("rotate");
        AssertContained();
        editor.Undo();
        editor.SetGeometry(child, new(new(500, 500), 1000, 1000, 30));
        AssertContained();

        void AssertContained() => Assert.True(editor.State.Rois[parent].Geometry.Covers(editor.State.Rois[child].Geometry));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FastTranslationCannotJumpAcrossAConcaveParentsGap(bool circle)
    {
        var editor = new EditorSession();
        var parent = editor.AddParent(RoiGeometry.Polygon([new(0, 0), new(400, 0), new(400, 400),
            new(260, 400), new(260, 100), new(140, 100), new(140, 400), new(0, 400)]));
        var group = editor.AddParent(new(new(70, 250), 40, 40, 0, circle ? RoiShape.Circle : RoiShape.Rectangle));
        editor.CommitPrepared(await editor.PrepareAttachAsync([group], parent));
        editor.Select([group]);
        editor.BeginPreview();
        foreach (var x in new[] { 80d, 260, 10000, 259, 40, 0, 260 })
        {
            editor.PreviewTranslation(new(x, 0));
            var shown = editor.TransformPreview!.Geometry(editor.State.Rois[group]);
            Assert.InRange(shown.Center.X, 70, 120.00001);
            Assert.True(editor.State.Rois[parent].Geometry.Covers(shown));
        }
        var preview = editor.TransformPreview!.Geometry(editor.State.Rois[group]);
        editor.CommitPreview("fast move");
        Assert.Equal(preview, editor.State.Rois[group].Geometry);
        editor.Undo();
        Assert.Equal(new PointD(70, 250), editor.State.Rois[group].Geometry.Center);
    }

    [Fact]
    public async Task FastRotationStopsAtFirstBoundaryInsteadOfJumpingToAnotherValidAngle()
    {
        var editor = new EditorSession();
        var parent = editor.AddParent(new(new(500, 500), 300, 100, 0));
        var group = editor.AddParent(new(new(500, 500), 250, 60, 0));
        editor.CommitPrepared(await editor.PrepareAttachAsync([group], parent));
        editor.Select([group]);
        editor.BeginPreview();
        foreach (var angle in new[] { 45d, 170, 180, 540, 179, 5, 0, -180 })
        {
            editor.PreviewRotation(new(500, 500), angle);
            Assert.InRange(editor.TransformPreview!.Transform.AngleDegrees, -15, 15);
            Assert.True(editor.State.Rois[parent].Geometry.Covers(editor.TransformPreview.Geometry(editor.State.Rois[group])));
        }
        var preview = editor.TransformPreview!.Geometry(editor.State.Rois[group]);
        editor.CommitPreview("fast rotate");
        Assert.Equal(preview, editor.State.Rois[group].Geometry);
    }
}
