using VisionCanvas.Core.Editing;
using VisionGeometry;

namespace TeachingToolWpf.Core.Tests;

public class GroupExpansionTests
{
    [Fact]
    public async Task AlreadyContainedAttachmentPreservesTheOriginalCircle()
    {
        var editor = new EditorSession();
        var parent = editor.AddParent(new(new(400, 400), 200, 200, 0, RoiShape.Circle));
        var child = editor.Add(new(new(400, 400), 20, 20, 0));
        var geometry = editor.State.Rois[parent].Geometry;
        editor.CommitPrepared(await editor.PrepareAttachAsync([child], parent));
        Assert.Same(geometry, editor.State.Rois[parent].Geometry);
        Assert.Equal(parent, editor.State.Rois[child].ParentId);
    }

    [Fact]
    public async Task RotatedExpansionOutsideImageRejectsWithoutClippingMembers()
    {
        var editor = new EditorSession();
        editor.InitializeImage(new("image", "image", 1000, 1000), ViewportTransform.Identity);
        var parent = editor.AddParent(new(new(500, 500), 300, 300, 45));
        var child = editor.Add(new(new(950, 950), 20, 20, 0));
        var before = editor.State;
        var bytes = editor.HistoryBytes;
        await Assert.ThrowsAsync<InvalidOperationException>(() => editor.PrepareAttachAsync([child], parent));
        Assert.Same(before, editor.State);
        Assert.Equal(bytes, editor.HistoryBytes);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 37)]
    [InlineData(true, 37)]
    public async Task GroupExpandsWithoutMovingMembersOrCreatingAnExtraRoi(bool prepared, double angle)
    {
        var editor = new EditorSession();
        var parent = editor.AddParent(new(new(400, 400), 200, 160, angle));
        var child = editor.AddChild(parent, new(new(400, 400), 20, 20, 0));
        var detached = editor.Add(new(new(700, 550), 60, 60, 0, RoiShape.Circle));
        editor.Select([parent, child, detached]);
        var before = editor.State;
        if (prepared)
        {
            var change = await editor.PrepareGroupAsync();
            Assert.Same(before, editor.State);
            editor.CommitPrepared(change);
        }
        else
        {
            Assert.Equal(parent, editor.GroupSelection());
        }
        var after = editor.State;
        Assert.Equal(before.Rois.Count, after.Rois.Count);
        Assert.Equal(angle, after.Rois[parent].Geometry.Angle);
        Assert.True(after.Rois[parent].Geometry.Covers(before.Rois[parent].Geometry));
        Assert.True(after.Rois[parent].Geometry.Covers(after.Rois[detached].Geometry));
        Assert.Equal(before.Rois[parent] with
        {
            Geometry = after.Rois[parent].Geometry
        }, after.Rois[parent]);
        Assert.Equal(before.Rois[child], after.Rois[child]);
        Assert.Equal(before.Rois[detached] with
        {
            ParentId = parent
        }, after.Rois[detached]);
        editor.Undo();
        Assert.Equal(before.Rois, editor.State.Rois);
        Assert.True(before.Selection.SetEquals(editor.State.Selection));
        editor.Redo();
        Assert.Equal(after.Rois, editor.State.Rois);
        var next = editor.Add(new(new(900, 900), 20, 20, 0));
        Assert.Equal(before.Rois.Values.Max(roi => roi.Order) + 1, editor.State.Rois[next].Order);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpansionOutsideAncestorRejectsTheWholeEditAndPreservesRedo(bool prepared)
    {
        var editor = new EditorSession();
        var outer = editor.AddParent(new(new(400, 400), 400, 400, 0));
        var parent = editor.AddChild(outer, new(new(400, 400), 100, 100, 0));
        editor.AddChild(parent, new(new(400, 400), 20, 20, 0));
        var detached = editor.Add(new(new(900, 400), 20, 20, 0));
        editor.Add(new(new(1000, 1000), 20, 20, 0));
        editor.Undo();
        editor.Select([parent, detached]);
        var before = editor.State;
        var history = editor.HistoryBytes;
        if (prepared)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => editor.PrepareGroupAsync());
        }
        else
        {
            Assert.Throws<InvalidOperationException>(() => editor.GroupSelection());
        }
        Assert.Same(before, editor.State);
        Assert.Equal(history, editor.HistoryBytes);
        Assert.True(editor.CanRedo);
    }

    [Theory]
    [InlineData(RoiShapeKind.Circle)]
    [InlineData(RoiShapeKind.Triangle)]
    [InlineData(RoiShapeKind.Polygon)]
    public async Task OutsideAttachmentConvertsOnlyTheParentToAnEnclosingRectangle(RoiShapeKind shape)
    {
        var editor = new EditorSession();
        var geometry = shape == RoiShapeKind.Polygon
            ? RoiGeometry.Polygon([new(300, 300), new(500, 300), new(500, 350), new(350, 350), new(350, 500), new(300, 500)])
            : RoiGeometry.Create(shape, new(400, 400), 200, 200);
        var parent = editor.AddParent(geometry);
        var detached = editor.Add(new(new(700, 400), 30, 30, 0, RoiShape.Circle));
        var member = editor.State.Rois[detached];
        editor.CommitPrepared(await editor.PrepareAttachAsync([detached], parent));
        var expanded = editor.State.Rois[parent].Geometry;
        Assert.Equal(RoiShapeKind.Rectangle, expanded.Shape.Kind);
        Assert.True(expanded.Covers(geometry));
        Assert.True(expanded.Covers(member.Geometry));
        Assert.Equal(member with
        {
            ParentId = parent
        }, editor.State.Rois[detached]);
        editor.Undo();
        Assert.Equal(geometry, editor.State.Rois[parent].Geometry);
    }
}
