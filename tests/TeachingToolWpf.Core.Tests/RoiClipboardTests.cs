using TeachingToolWpf.Core.Editing;
using TeachingToolWpf.Core.Geometry;

namespace TeachingToolWpf.Core.Tests;

public class RoiClipboardTests
{
    private static EditorSession Create(int size = 1000)
    {
        var editor = new EditorSession(); editor.InitializeImage(new("image", "image", size, size), ViewportTransform.Identity); return editor;
    }
    [Fact] public void CopyIncludesDescendantsOnceAndPasteHasNewIdsAndOneUndo()
    {
        var editor = Create();
        var parent = editor.Add(new(new(200, 200), 100, 100, 30));
        var child = editor.Add(new(new(200, 200), 20, 20, 30));
        editor.SetFilters(new(RoiFilterKind.Edge)); editor.Select([parent, child]);
        var clipboard = new RoiClipboard(); Assert.Equal(2, clipboard.Copy(editor.State));
        editor.Paste(clipboard);
        var pastedParent = Assert.Single(editor.State.Selection);
        var copies = editor.State.Rois.Values.Where(r => r.Id != parent && r.Id != child).ToArray();
        Assert.Equal(2, copies.Length); Assert.Contains(copies, r => r.Filter.Kind == RoiFilterKind.Edge);
        Assert.All(copies, r => Assert.Equal(30, r.Geometry.Angle));
        Assert.NotEqual(parent, pastedParent); Assert.True(RoiImageBounds.ContainsAll(editor.State));
        var pastedIds = copies.Select(r => r.Id).ToHashSet();
        editor.Undo(); Assert.Equal(2, editor.State.Rois.Count);
        editor.Redo(); Assert.True(pastedIds.All(editor.State.Rois.ContainsKey));
    }
    [Fact] public void SnapshotSurvivesSourceDeletionAndFitsSmallerImageAsOneAssembly()
    {
        var editor = Create();
        var a = editor.Add(new(new(100, 100), 50, 60, 20));
        var b = editor.Add(new(new(800, 800), 60, 40, 70)); editor.Select([a, b]);
        var clipboard = new RoiClipboard(); clipboard.Copy(editor.State);
        editor.DeleteSelection(); editor.SetImage(new("small", "small", 100, 100), ViewportTransform.Identity);
        editor.Paste(clipboard);
        Assert.Equal(2, editor.State.Rois.Count); Assert.True(RoiImageBounds.ContainsAll(editor.State));
        Assert.Equal(2, editor.State.Selection.Count);
        Assert.Equal(new[] { 20d, 70d }, editor.State.Rois.Values.OrderBy(r => r.Order).Select(r => r.Geometry.Angle));
    }
    [Fact] public void RepeatedPasteOffsetsCopiesAndEmptyPasteDoesNotAddHistory()
    {
        var editor = Create(); var clipboard = new RoiClipboard(); editor.Paste(clipboard); Assert.False(editor.CanUndo);
        editor.Add(new(new(100, 100), 40, 40, 0)); clipboard.Copy(editor.State);
        editor.Paste(clipboard); var first = editor.State.Rois[editor.State.Selection.Single()];
        editor.Paste(clipboard); var second = editor.State.Rois[editor.State.Selection.Single()];
        Assert.Equal(new PointD(20, 20), second.Geometry.Center - first.Geometry.Center);
        Assert.NotEqual(first.Id, second.Id);
    }
}
