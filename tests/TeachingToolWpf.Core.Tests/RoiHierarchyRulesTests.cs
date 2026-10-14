using System.Collections.Immutable;
using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Interaction;
using VisionGeometry;

namespace TeachingToolWpf.Core.Tests;

public class RoiHierarchyRulesTests
{
    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    public async Task ClipboardKeepsFourLevelsAndRejectsLegacyDeeperTrees(int depth)
    {
        var rois = ImmutableDictionary.CreateBuilder<Guid, Roi>();
        Guid? parent = null;
        for (var index = 0; index < depth; index++)
        {
            var roi = new Roi(Guid.NewGuid(), "ROI", new(new(100, 100), 40, 40, 0), parent, false, index);
            rois.Add(roi.Id, roi);
            parent = roi.Id;
        }
        var source = new DocumentState(rois.ToImmutable(), RoiIdSet.CreateRange(rois.Keys), null, ViewportTransform.Identity);
        var clipboard = new RoiClipboard();
        clipboard.Copy(source);
        var editor = new EditorSession();
        editor.InitializeImage(new("image", "image", 1000, 1000), ViewportTransform.Identity);
        var before = editor.State;
        if (depth == 5)
        {
            Assert.Throws<InvalidOperationException>(() => editor.Paste(clipboard));
            await Assert.ThrowsAsync<InvalidOperationException>(() => editor.PreparePasteAsync(clipboard));
            Assert.Same(before, editor.State);
            Assert.False(editor.CanUndo);
            Assert.Equal(1, clipboard.Capture().PasteNumber);
        }
        else
        {
            editor.Paste(clipboard);
            Assert.Equal(4, editor.State.Rois.Count);
            Assert.Equal(3, editor.State.Snapshot.GroupLevels[editor.State.Selection.Single()]);
            editor.Undo();
            Assert.Empty(editor.State.Rois);
            editor.Redo();
            Assert.Equal(4, editor.State.Rois.Count);
        }
    }

    [Theory]
    [InlineData(RoiShapeKind.Rectangle, 0)]
    [InlineData(RoiShapeKind.Rectangle, 45)]
    [InlineData(RoiShapeKind.Circle, 0)]
    [InlineData(RoiShapeKind.Triangle, 0)]
    public void ChildDrawingRejectsCrossingAndAllowsLaterOutsideEdits(RoiShapeKind shape, double angle)
    {
        var editor = new EditorSession();
        var geometry = RoiGeometry.Create(shape, new(200, 200), 200, 200);
        var parent = editor.Add(geometry.WithFrame(geometry.Center, geometry.Width, geometry.Height, angle));
        editor.Rename(parent, "수정");
        editor.Undo();
        var before = editor.State;
        var input = new InteractionController(editor) { Tool = EditorTool.Draw, DrawParentId = parent };
        input.Down(new(200, 200));
        Assert.Throws<ArgumentException>(() => input.Up(new(350, 350)));
        Assert.Same(before, editor.State);
        Assert.True(editor.CanRedo);
        Assert.Equal(EditorTool.Draw, input.Tool);

        input.Down(new(195, 195));
        input.Up(new(205, 205));
        var child = editor.State.Selection.Single();
        Assert.Equal(parent, editor.State.Rois[child].ParentId);
        editor.TranslateSelection(new(400, 0));
        Assert.False(editor.State.Rois[parent].Geometry.Contains(editor.State.Rois[child].Geometry));
        Assert.Equal(parent, editor.State.Rois[child].ParentId);
        editor.Undo();
        Assert.True(editor.State.Rois[parent].Geometry.Contains(editor.State.Rois[child].Geometry));
    }

    [Fact]
    public void ConcaveParentRejectsPolygonWithInsideVerticesAndOutsideEdge()
    {
        var editor = new EditorSession();
        var parent = editor.Add(RoiGeometry.Polygon([new(0, 0), new(100, 0), new(100, 100),
            new(60, 100), new(60, 40), new(40, 40), new(40, 100), new(0, 100)]));
        var input = new InteractionController(editor) { DrawShape = RoiShapeKind.Polygon, DrawParentId = parent, Tool = EditorTool.Draw };
        foreach (var point in new PointD[] { new(20, 80), new(80, 80), new(50, 20) })
        {
            input.Down(point);
            input.Up(point);
        }
        var before = editor.State;
        Assert.Throws<ArgumentException>(input.CompletePolygon);
        Assert.Same(before, editor.State);
        Assert.Equal(3, input.PolygonPointCount);
    }

    [Fact]
    public async Task FourLevelsBlockAllFifthLevelCreationPathsAndPreserveRedo()
    {
        var editor = new EditorSession();
        var leaf = editor.Add(new(new(100, 100), 20, 20, 0));
        var highest = leaf;
        for (var level = 1; level <= 3; level++)
        {
            var other = editor.Add(new(new(200 + (100 * level), 100), 20, 20, 0));
            if (level > 1)
            {
                editor.AddChild(other, new(new(200 + (100 * level), 100), 5, 5, 0));
            }
            editor.Select([highest, other]);
            editor.CommitPrepared(await editor.PrepareGroupAsync());
            highest = editor.State.Selection.Single();
            Assert.Equal(level, editor.State.Snapshot.GroupLevels[highest]);
        }
        Assert.Equal(4, RoiHierarchy.Depth(editor.State.Rois, leaf));
        Assert.StartsWith("루트 그룹", editor.State.Rois[highest].Name);
        var independent = editor.Add(new(new(700, 100), 20, 20, 0));
        editor.Select([highest, independent]);
        Assert.True(editor.State.SelectionFacts.CanGroup);
        var count = editor.State.Rois.Count;
        editor.GroupSelection();
        Assert.Equal(count, editor.State.Rois.Count);
        Assert.Equal(highest, editor.State.Rois[independent].ParentId);
        editor.Undo();
        editor.AddChild(independent, new(new(700, 100), 5, 5, 0));
        editor.Select([highest, independent]);
        editor.Rename(independent, "임시");
        editor.Undo();
        var before = editor.State;
        Assert.False(before.SelectionFacts.CanGroup);
        Assert.Throws<InvalidOperationException>(() => editor.GroupSelection());
        await Assert.ThrowsAsync<InvalidOperationException>(() => editor.PrepareGroupAsync());
        Assert.Throws<InvalidOperationException>(() => editor.AddChild(leaf, new(new(100, 100), 5, 5, 0)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => editor.PrepareAttachAsync([highest], independent));
        Assert.Same(before, editor.State);
        Assert.True(editor.CanRedo);
        var levels = editor.State.Snapshot.GroupLevels;
        editor.TranslateSelection(new(1, 0));
        Assert.Same(levels, editor.State.Snapshot.GroupLevels);
    }

    [Fact]
    public void FifthBatchLevelIsRejectedAtomically()
    {
        var editor = new EditorSession();
        editor.Add(new(new(10, 10), 5, 5, 0));
        editor.Undo();
        var before = editor.State;
        Assert.Throws<InvalidOperationException>(() => editor.AddRange(Enumerable.Range(0, 5)
            .Select(index => new RoiCreation(new(new(100, 100), 100 - (10 * index), 100 - (10 * index), 0), index == 0 ? null : index - 1))));
        Assert.Same(before, editor.State);
        Assert.True(editor.CanRedo);
    }

    [Fact]
    public async Task AttachPreservesGeometryAndHistoryAndRejectsCyclesOrReparenting()
    {
        var editor = new EditorSession();
        var parent = editor.Add(new(new(100, 100), 20, 20, 0));
        var child = editor.Add(new(new(300, 300), 10, 10, 0));
        var other = editor.Add(new(new(500, 500), 20, 20, 0));
        var geometry = editor.State.Rois[child].Geometry;
        editor.CommitPrepared(await editor.PrepareAttachAsync([child], parent));
        Assert.Equal(parent, editor.State.Rois[child].ParentId);
        Assert.Same(geometry, editor.State.Rois[child].Geometry);
        editor.Undo();
        Assert.Null(editor.State.Rois[child].ParentId);
        editor.Redo();
        var before = editor.State;
        await Assert.ThrowsAsync<InvalidOperationException>(() => editor.PrepareAttachAsync([child], other));
        await Assert.ThrowsAsync<InvalidOperationException>(() => editor.PrepareAttachAsync([parent], child));
        await Assert.ThrowsAsync<InvalidOperationException>(() => editor.PrepareAttachAsync([parent], parent));
        Assert.Same(before, editor.State);
        editor.Select([child, other]);
        Assert.False(editor.State.SelectionFacts.CanGroup);
        Assert.Throws<InvalidOperationException>(() => editor.GroupSelection());
        editor.Select([parent, other]);
        Assert.True(editor.State.SelectionFacts.CanGroup);
        editor.GroupSelection();
        Assert.Equal(parent, editor.State.Rois[child].ParentId);
    }
}
