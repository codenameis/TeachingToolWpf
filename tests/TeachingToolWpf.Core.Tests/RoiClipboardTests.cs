using TeachingToolWpf.Core.Editing;
using TeachingToolWpf.Core.Geometry;

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
