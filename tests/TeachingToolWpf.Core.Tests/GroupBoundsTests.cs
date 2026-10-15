using VisionCanvas.Core.Editing;
using VisionGeometry;

namespace TeachingToolWpf.Core.Tests;

public class GroupBoundsTests
{
    [Fact]
    public void ParentCreatedByLinkingCanAlsoBeUngrouped()
    {
        var editor = new EditorSession();
        var parent = editor.Add(new(new(100, 100), 100, 100, 0));
        var group = editor.AddChild(parent, new(new(100, 100), 50, 50, 0));
        editor.AddChild(group, new(new(100, 100), 10, 10, 0));
        editor.Select([parent]);
        Assert.True(editor.State.SelectionFacts.HasGroup);
        editor.UngroupSelection();
        Assert.Null(editor.State.Rois[group].ParentId);
        editor.TranslateSelection(new(200, 0));
        Assert.Equal(300, editor.State.Rois[group].Geometry.Center.X);
    }
    [Fact]
    public async Task OutsideCreationIsRejectedButAttachmentExpansionAllowsContainedPromotion()
    {
        var editor = new EditorSession();
        var parent = editor.AddParent(new(new(100, 100), 100, 100, 0));
        var before = editor.State;
        Assert.Throws<InvalidOperationException>(() => editor.AddChild(parent, new(new(300, 100), 5, 5, 0)));
        Assert.Same(before, editor.State);
        var another = editor.Add(new(new(300, 100), 5, 5, 0));
        editor.CommitPrepared(await editor.PrepareAttachAsync([another], parent));
        Assert.True(editor.State.Rois[parent].Geometry.Covers(editor.State.Rois[another].Geometry));
        var child = editor.AddChild(another, new(new(300, 100), 1, 1, 0));
        Assert.Equal(another, editor.State.Rois[child].ParentId);
        before = editor.State;
        Assert.Throws<InvalidOperationException>(() => editor.AddRange(new RoiCreation[]
        {
            new(new(new(700, 700), 100, 100, 0)),
            new(new(new(900, 900), 10, 10, 0), 0),
            new(new(new(900, 900), 5, 5, 0), 1)
        }));
        Assert.Same(before, editor.State);
    }

    [Fact]
    public async Task ZeroAngleResetCannotBypassTheGroupBoundary()
    {
        var editor = new EditorSession();
        var parent = editor.AddParent(new(new(500, 500), 300, 100, 90));
        var group = editor.AddParent(new(new(500, 500), 250, 60, 90));
        editor.CommitPrepared(await editor.PrepareAttachAsync([group], parent));
        editor.Select([group]);
        var before = editor.State;
        Assert.Throws<InvalidOperationException>(editor.ResetAngles);
        Assert.Same(before, editor.State);
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task GroupTranslationIsConstrainedAndHistoryPreservesHierarchy(bool module, bool prepared)
    {
        var (editor, parent, group, leaf) = await Create(module);
        var before = editor.State;
        if (prepared)
        {
            editor.CommitPrepared(await editor.PrepareTranslationAsync(new(500, 0)));
        }
        else
        {
            editor.TranslateSelection(new(500, 0));
        }
        AssertContained(editor, parent, group);
        Assert.NotEqual(before.Rois[group].Geometry, editor.State.Rois[group].Geometry);
        Assert.Equal(parent, editor.State.Rois[group].ParentId);
        var movement = editor.State.Rois[group].Geometry.Center - before.Rois[group].Geometry.Center;
        Assert.Equal(movement, editor.State.Rois[leaf].Geometry.Center - before.Rois[leaf].Geometry.Center);
        var after = editor.State;
        editor.Undo();
        Assert.Equal(before.Rois, editor.State.Rois);
        editor.Redo();
        Assert.Equal(after.Rois, editor.State.Rois);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RotationPreviewAndCommitStayInsideActualRotatedParent(bool prepared)
    {
        var editor = new EditorSession();
        var parent = editor.AddParent(new(new(500, 500), 300, 100, 30));
        var group = editor.AddParent(new(new(500, 500), 250, 60, 30));
        editor.CommitPrepared(await editor.PrepareAttachAsync([group], parent));
        editor.Select([group]);
        var before = editor.State;
        editor.BeginPreview();
        editor.PreviewRotation(new(500, 500), 90);
        Assert.True(before.Rois[parent].Geometry.Covers(editor.TransformPreview!.Geometry(before.Rois[group])));
        if (prepared)
        {
            editor.CommitPrepared(await editor.PreparePreviewAsync("회전"));
        }
        else
        {
            editor.CommitPreview("회전");
        }
        AssertContained(editor, parent, group);
        Assert.InRange(editor.State.Rois[group].Geometry.Angle, 30.1, 50);
        editor.Undo();
        Assert.Equal(before.Rois, editor.State.Rois);
        editor.CommitPrepared(await editor.PrepareRotationAsync(90, new(500, 500)));
        AssertContained(editor, parent, group);
    }

    [Fact]
    public async Task ResizeAndNumericEditsConstrainChildGrowthAndParentShrink()
    {
        var (editor, parent, group, _) = await Create(false);
        var geometry = editor.State.Rois[group].Geometry;
        editor.BeginPreview();
        editor.PreviewResize(group, geometry.WithFrame(geometry.Center, 900, 900, 0));
        AssertContained(editor, parent, group);
        editor.CommitPreview("크기 변경");
        editor.Undo();
        editor.SetProperties(group, "bounded", geometry.WithFrame(new(950, 500), 700, 700, 0));
        AssertContained(editor, parent, group);
        Assert.Equal("bounded", editor.State.Rois[group].Name);
        editor.Undo();
        var container = editor.State.Rois[parent].Geometry;
        editor.SetGeometry(parent, container.WithFrame(container.Center, 10, 10, 0));
        AssertContained(editor, parent, group);
        Assert.True(editor.State.Rois[parent].Geometry.Width >= geometry.Width - 1e-5);
    }

    [Fact]
    public async Task MovingParentAndChildTogetherDoesNotBlockTheWholeAssembly()
    {
        var (editor, parent, group, _) = await Create(false);
        editor.Select([parent, group]);
        editor.TranslateSelection(new(500, 0));
        AssertContained(editor, parent, group);
        Assert.Equal(1000, editor.State.Rois[group].Geometry.Center.X);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UngroupReleasesTheGroupAndKeepsItsLeavesContained(bool prepared)
    {
        var (editor, parent, group, leaf) = await Create(false);
        editor.Select([leaf]);
        editor.TranslateSelection(new(700, 0));
        Assert.True(editor.State.Rois[group].Geometry.Covers(editor.State.Rois[leaf].Geometry));
        editor.Select([parent]);
        var before = editor.State;
        if (prepared)
        {
            editor.CommitPrepared(await editor.PrepareUngroupAsync());
        }
        else
        {
            editor.UngroupSelection();
        }
        Assert.Null(editor.State.Rois[group].ParentId);
        Assert.Equal(group, editor.State.Rois[leaf].ParentId);
        editor.TranslateSelection(new(700, 0));
        Assert.False(before.Rois[parent].Geometry.Covers(editor.State.Rois[group].Geometry));
        editor.Undo();
        editor.Undo();
        Assert.Equal(before.Rois, editor.State.Rois);
    }

    [Fact]
    public async Task AttachmentExpandsForOutsideLeafAndAllowsEqualGroupBoundaries()
    {
        var editor = new EditorSession();
        var parent = editor.AddParent(new(new(100, 100), 100, 100, 0));
        var group = editor.AddParent(new(new(300, 100), 20, 20, 0));
        var before = editor.State;
        await Assert.ThrowsAsync<InvalidOperationException>(() => editor.PrepareAttachAsync([group], parent));
        Assert.Same(before, editor.State);
        var leaf = editor.Add(new(new(300, 100), 10, 10, 0));
        var outside = editor.State.Rois[leaf].Geometry;
        editor.CommitPrepared(await editor.PrepareAttachAsync([leaf], parent));
        Assert.Equal(parent, editor.State.Rois[leaf].ParentId);
        Assert.Equal(outside, editor.State.Rois[leaf].Geometry);
        AssertContained(editor, parent, leaf);
        editor.Select([leaf]);
        editor.TranslateSelection(new(-200, 0));
        AssertContained(editor, parent, leaf);
        var inside = editor.State;
        editor.TranslateSelection(new(500, 0));
        AssertContained(editor, parent, leaf);
        editor.Undo();
        Assert.Equal(inside.Rois, editor.State.Rois);
        editor.Undo();
        Assert.Equal(outside, editor.State.Rois[leaf].Geometry);
        editor.SetGeometry(group, editor.State.Rois[parent].Geometry);
        editor.CommitPrepared(await editor.PrepareAttachAsync([group], parent));
        AssertContained(editor, parent, group);
    }

    [Theory]
    [InlineData(RoiShapeKind.Circle)]
    [InlineData(RoiShapeKind.Triangle)]
    [InlineData(RoiShapeKind.Polygon)]
    public async Task GroupCannotMoveIntoAnEmptyCornerOfItsParentsBounds(RoiShapeKind shape)
    {
        var editor = new EditorSession();
        var geometry = shape == RoiShapeKind.Polygon
            ? RoiGeometry.Polygon([new(0, 0), new(400, 0), new(400, 150), new(150, 150), new(150, 400), new(0, 400)])
            : RoiGeometry.Create(shape, new(200, 200), 400, 400);
        var parent = editor.AddParent(geometry);
        var group = editor.AddParent(new(shape == RoiShapeKind.Polygon ? new(80, 80) : new(200, 200), 20, 20, 0));
        editor.CommitPrepared(await editor.PrepareAttachAsync([group], parent));
        editor.Select([group]);
        editor.BeginPreview();
        editor.PreviewTranslation(new(250, 250));
        Assert.True(geometry.Covers(editor.TransformPreview!.Geometry(editor.State.Rois[group])));
        editor.CommitPreview("이동");
        AssertContained(editor, parent, group);
    }

    private static async Task<(EditorSession Editor, Guid Parent, Guid Group, Guid Leaf)> Create(bool module)
    {
        var editor = new EditorSession();
        var parent = editor.AddParent(new(new(500, 500), 400, 400, 0));
        var group = editor.AddParent(new(new(500, 500), 100, 100, 0));
        var inner = group;
        if (module)
        {
            inner = editor.AddParent(new(new(500, 500), 40, 40, 0));
            editor.CommitPrepared(await editor.PrepareAttachAsync([inner], group));
        }
        var leaf = editor.AddChild(inner, new(new(500, 500), 10, 10, 0));
        editor.CommitPrepared(await editor.PrepareAttachAsync([group], parent));
        editor.Select([group]);
        return (editor, parent, group, leaf);
    }

    private static void AssertContained(EditorSession editor, Guid parent, Guid child)
        => Assert.True(editor.State.Rois[parent].Geometry.Covers(editor.State.Rois[child].Geometry));
}
