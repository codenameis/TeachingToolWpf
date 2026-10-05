using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Geometry;
using VisionGeometry;

namespace TeachingToolWpf.Core.Tests;

public class RoiClipboardTests
{
    private static EditorSession Create(int size = 1000)
    {
        var editor = new EditorSession();
        editor.InitializeImage(new("image", "image", size, size), ViewportTransform.Identity);
        return editor;
    }

    [Fact]
    public void CopyIncludesDescendantsOnceAndPasteHasNewIdsAndOneUndo()
    {
        var editor = Create();
        var parent = editor.Add(new(new(200, 200), 100, 100, 30));
        var child = editor.AddChild(parent, new(new(200, 200), 20, 20, 30));
        editor.SetFilters(new(RoiFilterKind.Edge));
        editor.Select([parent, child]);
        var clipboard = new RoiClipboard();
        Assert.Equal(2, clipboard.Copy(editor.State));
        editor.Paste(clipboard);
        var pastedParent = Assert.Single(editor.State.Selection);
        var copies = editor.State.Rois.Values.Where(r => r.Id != parent && r.Id != child).ToArray();
        Assert.Equal(2, copies.Length);
        Assert.Contains(copies, r => r.Filter.Kind == RoiFilterKind.Edge);
        Assert.All(copies, r => Assert.Equal(30, r.Geometry.Angle));
        Assert.NotEqual(parent, pastedParent);
        Assert.True(RoiImageBounds.ContainsAll(editor.State));
        var pastedIds = copies.Select(r => r.Id).ToHashSet();
        editor.Undo();
        Assert.Equal(2, editor.State.Rois.Count);
        editor.Redo();
        Assert.True(pastedIds.All(editor.State.Rois.ContainsKey));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CopyPreservesSparseOrderAndLaterParentWithoutDuplicatingSelectedDescendants(bool copyAll)
    {
        var editor = Create();
        var unrelatedFirst = editor.Add(new(new(800, 800), 20, 20, 0));
        var firstChild = editor.Add(new(new(100, 100), 20, 20, 15));
        var deleted = editor.Add(new(new(300, 300), 20, 20, 0));
        var secondChild = editor.Add(new(new(150, 100), 20, 20, 30));
        editor.Select([firstChild, secondChild]);
        var parent = editor.GroupSelection();
        var unrelatedLast = editor.Add(new(new(900, 900), 20, 20, 0));
        editor.Select([deleted]);
        editor.DeleteSelection();
        editor.Select(copyAll ? editor.State.Rois.Keys : [parent, firstChild, parent, firstChild]);
        var before = editor.State;
        var clipboard = new RoiClipboard();
        Guid[] expectedIds = copyAll ? [unrelatedFirst, firstChild, secondChild, parent, unrelatedLast]
            : [firstChild, secondChild, parent];

        Assert.Equal(expectedIds.Length, clipboard.Copy(before));
        var entries = clipboard.Capture().Entries;
        Assert.Equal(expectedIds, entries.Select(roi => roi.Id));
        Assert.Equal(copyAll ? new long[] { 1, 2, 4, 5, 6 } : [2, 4, 5], entries.Select(roi => roi.Order));
        Assert.Equal(parent, entries.Single(roi => roi.Id == firstChild).ParentId);
        Assert.Equal(parent, entries.Single(roi => roi.Id == secondChild).ParentId);
        Assert.Null(entries.Single(roi => roi.Id == parent).ParentId);
        Assert.All(entries, roi => Assert.Same(before.Rois[roi.Id], roi));
        Assert.Same(before, editor.State);
    }

    [Fact]
    public void SnapshotSurvivesSourceDeletionAndFitsSmallerImageAsOneAssembly()
    {
        var editor = Create();
        var a = editor.Add(new(new(100, 100), 50, 60, 20));
        var b = editor.Add(new(new(800, 800), 60, 40, 70));
        editor.Select([a, b]);
        var clipboard = new RoiClipboard();
        clipboard.Copy(editor.State);
        editor.DeleteSelection();
        editor.SetImage(new("small", "small", 100, 100), ViewportTransform.Identity);
        editor.Paste(clipboard);
        Assert.Equal(2, editor.State.Rois.Count);
        Assert.True(RoiImageBounds.ContainsAll(editor.State));
        Assert.Equal(2, editor.State.Selection.Count);
        Assert.Equal(new[] { 20d, 70d }, editor.State.Rois.Values.OrderBy(r => r.Order).Select(r => r.Geometry.Angle));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PasteMatchesCapturedPlacementForRotatedHierarchyAndSmallerImage(bool asynchronous)
    {
        var editor = Create();
        var first = editor.Add(new(new(200, 250), 80, 120, 37, RoiShape.Triangle));
        editor.Rename(first, new string('A', 256));
        var second = editor.Add(new(new(650, 600), 90, 90, 73, RoiShape.Circle));
        var polygon = RoiGeometry.Polygon([new(300, 300), new(420, 300), new(360, 360), new(420, 420), new(300, 420)]);
        var third = editor.Add(polygon.RotateAround(polygon.Center, 23));
        editor.Select([first, second, third]);
        editor.SetFilters(new(RoiFilterKind.Edge));
        var parent = editor.GroupSelection();
        var clipboard = new RoiClipboard();
        Assert.Equal(4, clipboard.Copy(editor.State));
        var content = clipboard.Capture();
        Assert.Equal(parent, content.Entries[^1].Id);
        editor.SetImage(new("small", "small", 100, 80), ViewportTransform.Identity);
        var expected = content.Place(editor.State.Image!, new(20, 20));

        if (asynchronous)
        {
            await editor.PasteAsync(clipboard);
        }
        else
        {
            editor.Paste(clipboard);
        }

        var pasted = editor.State;
        var copies = pasted.Rois.Values.OrderBy(roi => roi.Order).ToArray();
        Assert.Equal(expected.Length, copies.Length);
        var ids = content.Entries.Select((roi, index) => (roi.Id, CopyId: copies[index].Id))
            .ToDictionary(pair => pair.Id, pair => pair.CopyId);
        for (var i = 0; i < copies.Length; i++)
        {
            var source = expected[i];
            var copy = copies[i];
            Assert.DoesNotContain(copy.Id, ids.Keys);
            Assert.Equal(source.Geometry, copy.Geometry);
            Assert.Same(source.Geometry.Shape, copy.Geometry.Shape);
            Assert.Equal(source.Filter, copy.Filter);
            Assert.Equal(source.IsGroup, copy.IsGroup);
            Assert.Equal(source.Name[..Math.Min(source.Name.Length, 253)] + " 복사", copy.Name);
            Assert.Equal(source.ParentId is { } parentId ? ids[parentId] : (Guid?)null, copy.ParentId);
            Assert.Equal(copies[0].Order + i, copy.Order);
        }
        Assert.Equal(ids[parent], Assert.Single(pasted.Selection));
        Assert.True(RoiImageBounds.ContainsAll(pasted));
        Assert.Equal(2, clipboard.Capture().PasteNumber);
        editor.Undo();
        Assert.Empty(editor.State.Rois);
        editor.Redo();
        Assert.True(pasted.Selection.SetEquals(editor.State.Selection));
        Assert.All(pasted.Rois, pair => Assert.Equal(pair.Value, editor.State.Rois[pair.Key]));
    }

    [Fact]
    public void CopyReusesSpatialBoundsOnlyForAllSelectedShapes()
    {
        var editor = Create();
        var triangle = editor.Add(new(new(200, 200), 100, 80, 37, RoiShape.Triangle));
        editor.Add(new(new(650, 700), 90, 90, 73, RoiShape.Circle));
        editor.Add(RoiGeometry.Polygon([new(400, 400), new(500, 400), new(440, 440), new(500, 500), new(400, 500)]));
        editor.Select(editor.State.Rois.Keys);
        var clipboard = new RoiClipboard();
        clipboard.Copy(editor.State);
        var cold = clipboard.Capture();

        _ = editor.State.Snapshot.Spatial;
        clipboard.Copy(editor.State);
        var warm = clipboard.Capture();
        Assert.Equal(cold.Bounds, warm.Bounds);
        Assert.Equal(cold.Entries.ToArray(), warm.Entries.ToArray());

        editor.Select([triangle]);
        clipboard.Copy(editor.State);
        Assert.Equal(editor.State.Rois[triangle].Geometry.Bounds, clipboard.Capture().Bounds);
        Assert.NotEqual(warm.Bounds, clipboard.Capture().Bounds);
    }

    [Fact]
    public void RepeatedPasteOffsetsCopiesAndEmptyPasteDoesNotAddHistory()
    {
        var editor = Create();
        var clipboard = new RoiClipboard();
        editor.Paste(clipboard);
        Assert.False(editor.CanUndo);
        editor.Add(new(new(100, 100), 40, 40, 0));
        clipboard.Copy(editor.State);
        editor.Paste(clipboard);
        var first = editor.State.Rois[editor.State.Selection.Single()];
        editor.Paste(clipboard);
        var second = editor.State.Rois[editor.State.Selection.Single()];
        Assert.Equal(new PointD(20, 20), second.Geometry.Center - first.Geometry.Center);
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public void BulkOverlappingPastePreservesValuesAndAtomicHistory()
    {
        var editor = Create();
        editor.AddRange(Enumerable.Repeat(new RoiGeometry(new(200, 200), 100, 100, 0), 7000));
        editor.Select(editor.State.Rois.Keys);
        var original = editor.State;
        var clipboard = new RoiClipboard();
        Assert.Equal(7000, clipboard.Copy(original));
        editor.Paste(clipboard);
        var pasted = editor.State;
        Assert.Equal(14000, pasted.Rois.Count);
        Assert.Equal(7000, pasted.Selection.Count);
        Assert.All(pasted.Rois.Values, roi => Assert.Null(roi.ParentId));
        Assert.All(original.Rois, pair => Assert.Equal(pair.Value, pasted.Rois[pair.Key]));
        Assert.Empty(original.Selection.Intersect(pasted.Selection));
        editor.Undo();
        Assert.True(editor.State.Selection.SetEquals(original.Selection));
        Assert.Equal(7000, editor.State.Rois.Count);
        editor.Redo();
        Assert.True(editor.State.Selection.SetEquals(pasted.Selection));
        Assert.All(pasted.Rois, pair => Assert.Equal(pair.Value, editor.State.Rois[pair.Key]));
    }

    [Fact]
    public async Task AsyncPasteHasOneUndoAndCancelledPastePreservesRedo()
    {
        var editor = Create();
        editor.AddRange(Enumerable.Repeat(new RoiGeometry(new(200, 200), 30, 30, 0), 300));
        editor.Select(editor.State.Rois.Keys);
        var original = editor.State;
        var clipboard = new RoiClipboard();
        clipboard.Copy(original);
        await editor.PasteAsync(clipboard);
        Assert.Equal(600, editor.State.Rois.Count);
        editor.Undo();
        Assert.Equal(300, editor.State.Rois.Count);
        Assert.True(editor.CanRedo);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var before = editor.State;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => editor.PasteAsync(clipboard, cancellation.Token));
        Assert.Same(before, editor.State);
        Assert.True(editor.CanRedo);
        editor.Redo();
        Assert.Equal(600, editor.State.Rois.Count);
    }
}
