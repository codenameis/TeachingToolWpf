using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Geometry;
using VisionGeometry;

namespace TeachingToolWpf.Core.Tests;

public class PreparedGroupingTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ExistingParentAcceptsDetachedRoiWithoutCreatingAnotherRoi(bool asynchronous, bool nested)
    {
        var editor = Create();
        var root = editor.Add(new(new(400, 400), 200, 200, 0));
        var parent = nested ? editor.AddChild(root, new(new(400, 400), 100, 100, 25)) : root;
        var child = editor.AddChild(parent, new(new(400, 400), 10, 10, 0));
        var detached = editor.Add(new(new(1500, 1500), 40, 30, 60));
        editor.Select([parent, child, detached]);
        var before = editor.State;
        Assert.True(before.SelectionFacts.CanGroup);

        if (asynchronous)
        {
            var prepared = await editor.PrepareGroupAsync();
            Assert.Same(before, editor.State);
            editor.CommitPrepared(prepared);
        }
        else
        {
            Assert.Equal(parent, editor.GroupSelection());
        }

        var after = editor.State;
        Assert.Equal(before.Rois.Count, after.Rois.Count);
        Assert.Equal(parent, Assert.Single(after.Selection));
        Assert.Equal(before.Rois[parent], after.Rois[parent]);
        Assert.Equal(before.Rois[child], after.Rois[child]);
        Assert.Equal(before.Rois[detached] with
        {
            ParentId = parent
        }, after.Rois[detached]);
        Assert.False(after.Rois[parent].Geometry.Contains(after.Rois[detached].Geometry));
        editor.Undo();
        AssertDocument(before, editor.State);
        editor.Redo();
        AssertDocument(after, editor.State);
        var next = editor.Add(new(new(1700, 1700), 20, 20, 0));
        Assert.Equal(before.Rois.Values.Max(roi => roi.Order) + 1, editor.State.Rois[next].Order);
    }

    private static EditorSession Create()
    {
        var editor = new EditorSession();
        editor.InitializeImage(new("group", "group", 2048, 2048), ViewportTransform.Identity);
        return editor;
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task WholeGroupPreservesShapesDescendantsHistoryAndNextOrder(bool asynchronous, bool warm)
    {
        var editor = Create();
        var parent = editor.Add(new(new(400, 400), 100, 60, 37));
        var outsideChild = editor.AddChild(parent, new(new(900, 600), 80, 80, 63, RoiShape.Circle));
        var otherParent = editor.Add(new(new(1200, 1100), 140, 100, 51, RoiShape.Triangle));
        editor.AddChild(otherParent, new(new(1200, 1100), 10, 10, 0));
        editor.Add(RoiGeometry.Polygon([new(600, 700), new(720, 700), new(660, 750), new(720, 820), new(600, 820)]));
        editor.Select(editor.State.Rois.Keys);
        var before = editor.State;
        var expectedBounds = new RoiSpatialIndex(before.Rois.Values).Bounds;
        if (warm)
        {
            _ = before.Snapshot.Spatial;
        }
        var bytes = editor.HistoryBytes;
        var view = before.Viewport.Pan(new(70, -40));
        if (asynchronous)
        {
            var pending = editor.PrepareGroupAsync();
            editor.SetViewport(view);
            var prepared = await pending;
            Assert.Same(before.Rois, editor.State.Rois);
            Assert.Equal(bytes, editor.HistoryBytes);
            editor.CommitPrepared(prepared);
        }
        else
        {
            editor.SetViewport(view);
            editor.GroupSelection();
        }

        var after = editor.State;
        var group = after.Rois[Assert.Single(after.Selection)];
        Assert.True(group.IsGroup);
        Assert.Null(group.ParentId);
        Assert.Equal(before.Rois.Values.Max(roi => roi.Order) + 1, group.Order);
        Assert.Equal(new RoiGeometry(expectedBounds.Center, expectedBounds.Width + 2, expectedBounds.Height + 2, 0), group.Geometry);
        Assert.Equal(parent, after.Rois[outsideChild].ParentId);
        Assert.All(before.Rois.Values, roi =>
        {
            Assert.Equal(roi with
            {
                ParentId = roi.ParentId ?? group.Id
            }, after.Rois[roi.Id]);
            Assert.Same(roi.Geometry, after.Rois[roi.Id].Geometry);
        });
        Assert.Equal(view, after.Viewport);
        editor.Undo();
        AssertDocument(before, editor.State);
        Assert.Equal(view, editor.State.Viewport);
        editor.Redo();
        AssertDocument(after, editor.State);
        var next = editor.Add(new(new(1700, 1700), 20, 20, 0));
        Assert.Equal(group.Order + 1, editor.State.Rois[next].Order);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartialGroupUsesSubtreeBoundsAndPreservesUnrelatedRoots(bool asynchronous)
    {
        var editor = Create();
        var parent = editor.Add(new(new(1000, 1000), 1800, 1800, 0));
        var left = editor.Add(new(new(300, 400), 30, 60, 15));
        var child = editor.AddChild(left, new(new(600, 700), 40, 40, 0, RoiShape.Circle));
        var right = editor.Add(new(new(450, 500), 70, 80, 65, RoiShape.Triangle));
        var rightChild = editor.AddChild(right, new(new(450, 500), 10, 10, 0));
        var unrelated = editor.Add(new(new(1500, 1500), 80, 80, 0));
        editor.Select([left, child, right]);
        var before = editor.State;
        _ = before.Snapshot.Spatial;
        var expected = new RoiSpatialIndex(new[] { left, child, right, rightChild }.Select(id => before.Rois[id])).Bounds;
        if (asynchronous)
        {
            editor.CommitPrepared(await editor.PrepareGroupAsync());
        }
        else
        {
            editor.GroupSelection();
        }

        var group = editor.State.Rois[Assert.Single(editor.State.Selection)];
        Assert.Null(group.ParentId);
        Assert.Equal(before.Rois[parent], editor.State.Rois[parent]);
        Assert.Equal(new RoiGeometry(expected.Center, expected.Width + 2, expected.Height + 2, 0), group.Geometry);
        Assert.Equal(group.Id, editor.State.Rois[left].ParentId);
        Assert.Equal(group.Id, editor.State.Rois[right].ParentId);
        Assert.Equal(left, editor.State.Rois[child].ParentId);
        Assert.Equal(before.Rois[unrelated], editor.State.Rois[unrelated]);
        editor.Undo();
        AssertDocument(before, editor.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NestedUngroupRestoresSurvivingAncestorInOneEdit(bool asynchronous)
    {
        var editor = Create();
        var parent = editor.Add(new(new(800, 800), 1000, 1000, 0));
        var first = editor.Add(new(new(400, 400), 30, 30, 15));
        var second = editor.Add(new(new(500, 500), 30, 30, 25));
        editor.Select([first, second]);
        var inner = editor.GroupSelection();
        var third = editor.Add(new(new(600, 600), 30, 30, 35));
        var fourth = editor.AddChild(third, new(new(600, 600), 10, 10, 0));
        editor.Select([inner, third]);
        var outer = editor.GroupSelection();
        editor.CommitPrepared(await editor.PrepareAttachAsync([outer], parent));
        editor.Select([inner, outer]);
        var before = editor.State;
        var view = before.Viewport.Pan(new(40, 50));
        if (asynchronous)
        {
            var pending = editor.PrepareUngroupAsync();
            editor.SetViewport(view);
            var prepared = await pending;
            Assert.Same(before.Rois, editor.State.Rois);
            editor.CommitPrepared(prepared);
        }
        else
        {
            editor.SetViewport(view);
            editor.UngroupSelection();
        }

        var after = editor.State;
        Assert.Equal(5, after.Rois.Count);
        Assert.Equal(third, after.Rois[fourth].ParentId);
        Assert.True(after.Selection.SetEquals([first, second, third]));
        Assert.All(after.Selection, id => Assert.Equal(before.Rois[id] with { ParentId = parent }, after.Rois[id]));
        Assert.Equal(view, after.Viewport);
        editor.Undo();
        AssertDocument(before, editor.State);
        editor.Redo();
        AssertDocument(after, editor.State);
        var next = editor.Add(new(new(1700, 1700), 20, 20, 0));
        Assert.Equal(before.Rois.Values.Max(roi => roi.Order) + 1, editor.State.Rois[next].Order);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledGroupingPreparationPreservesDocumentRedoAndOrder(bool ungroup)
    {
        var editor = Create();
        var first = editor.Add(new(new(400, 400), 30, 30, 0));
        editor.Add(new(new(600, 600), 30, 30, 0));
        editor.Select(editor.State.Rois.Keys);
        if (ungroup)
        {
            editor.GroupSelection();
        }
        editor.Rename(first, "changed");
        editor.Undo();
        var before = editor.State;
        var bytes = editor.HistoryBytes;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ungroup
            ? editor.PrepareUngroupAsync(cancellation.Token) : editor.PrepareGroupAsync(cancellation.Token));
        Assert.Same(before, editor.State);
        Assert.Equal(bytes, editor.HistoryBytes);
        Assert.True(editor.CanRedo);
        var next = editor.Add(new(new(1700, 1700), 20, 20, 0));
        Assert.Equal(before.Rois.Values.Max(roi => roi.Order) + 1, editor.State.Rois[next].Order);
    }

    [Fact]
    public async Task BulkGroupingAndUngroupingPreserveEveryRoiAndPrepareTheWholeSubtree()
    {
        var editor = Create();
        editor.AddRange(Enumerable.Range(0, 5000).Select(index =>
            new RoiGeometry(new(100 + (index % 100 * 10), 100 + (index / 100 * 10)), 4, 4, 0)));
        editor.Select(editor.State.Rois.Keys);
        var before = editor.State;
        var grouped = await editor.PrepareGroupAsync();
        Assert.Same(before, editor.State);
        Assert.Equal(5001, grouped.State.TransformSelection.Affected.Count);
        Assert.True(grouped.State.IsTransformSelectionPrepared);
        var group = Assert.Single(grouped.State.Selection);
        Assert.Equal(5000, grouped.State.Snapshot.Children[group].Length);
        editor.CommitPrepared(grouped);
        var ungrouped = await editor.PrepareUngroupAsync();
        Assert.Equal(5001, editor.State.Rois.Count);
        AssertDocument(before, ungrouped.State);
        editor.CommitPrepared(ungrouped);
        AssertDocument(before, editor.State);
        editor.Undo();
        AssertDocument(grouped.State, editor.State);
    }

    [Fact]
    public async Task StaleOrAbandonedGroupDoesNotConsumeOrderAndSuccessfulGroupCommitsOnce()
    {
        var editor = Create();
        editor.Add(new(new(400, 400), 30, 30, 0));
        editor.Add(new(new(600, 600), 30, 30, 0));
        editor.Select(editor.State.Rois.Keys);
        var stale = await editor.PrepareGroupAsync();
        editor.Select([]);
        Assert.Throws<InvalidOperationException>(() => editor.CommitPrepared(stale));
        editor.Select(editor.State.Rois.Keys);
        var prepared = await editor.PrepareGroupAsync();
        Assert.Equal(3, prepared.State.Rois[prepared.State.Selection.Single()].Order);
        editor.CommitPrepared(prepared);
        var after = editor.State;
        Assert.Throws<InvalidOperationException>(() => editor.CommitPrepared(prepared));
        Assert.Same(after, editor.State);
        var next = editor.Add(new(new(1700, 1700), 20, 20, 0));
        Assert.Equal(4, editor.State.Rois[next].Order);
    }

    [Fact]
    public async Task InvalidGroupAndEmptyUngroupPreserveSelectionAndRedo()
    {
        var editor = Create();
        var id = editor.Add(new(new(400, 400), 30, 30, 0));
        editor.Rename(id, "changed");
        editor.Undo();
        var before = editor.State;
        var bytes = editor.HistoryBytes;
        await Assert.ThrowsAsync<InvalidOperationException>(() => editor.PrepareGroupAsync());
        var prepared = await editor.PrepareUngroupAsync();
        Assert.Same(before, prepared.State);
        editor.CommitPrepared(prepared);
        AssertDocument(before, editor.State);
        Assert.Equal(bytes, editor.HistoryBytes);
        Assert.True(editor.CanRedo);
    }

    private static void AssertDocument(DocumentState expected, DocumentState actual)
    {
        Assert.Equal(expected.Rois.Count, actual.Rois.Count);
        Assert.All(expected.Rois, pair => Assert.Equal(pair.Value, actual.Rois[pair.Key]));
        Assert.True(expected.Selection.SetEquals(actual.Selection));
        Assert.Equal(expected.Image, actual.Image);
    }
}
