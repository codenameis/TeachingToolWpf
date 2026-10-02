using TeachingToolWpf.Core.Editing;
using TeachingToolWpf.Core.Geometry;

namespace TeachingToolWpf.Core.Tests;

public class TranslationPreviewTests
{
    [Fact] public void PreviewRetainsDocumentAndMovesDescendantsOnceThenCommitsOneHistoryEntry()
    {
        var editor = new EditorSession(); editor.InitializeImage(new("image", "image", 1000, 1000), ViewportTransform.Identity);
        var parent = editor.Add(new(new(300, 300), 200, 200, 15));
        var child = editor.Add(new(new(310, 300), 30, 20, 25));
        var other = editor.Add(new(new(800, 800), 20, 20, 0));
        editor.Select([parent, child]); var before = editor.State; var history = editor.HistoryBytes;
        editor.BeginPreview(); editor.PreviewTranslation(new(50, 20));
        var selection = editor.TranslationPreview!.Selection;
        for (var i = 0; i < 100; i++) editor.PreviewTranslation(new(i, i / 2d));
        Assert.Same(before, editor.State); Assert.Same(selection, editor.TranslationPreview!.Selection); Assert.Equal(history, editor.HistoryBytes);
        var delta = editor.TranslationPreview.Delta;
        Assert.Equal(before.Rois[child].Geometry.Translate(delta), editor.TranslationPreview.Geometry(before.Rois[child]));
        Assert.DoesNotContain(other, selection.Affected);
        editor.CommitPreview("이동");
        Assert.Null(editor.TranslationPreview);
        Assert.Equal(before.Rois[parent].Geometry.Translate(delta), editor.State.Rois[parent].Geometry);
        Assert.Equal(before.Rois[child].Geometry.Translate(delta), editor.State.Rois[child].Geometry);
        Assert.Equal(before.Rois[other], editor.State.Rois[other]);
        var after = editor.State; editor.Undo(); Assert.Equal(before.Rois, editor.State.Rois);
        editor.Redo(); Assert.Equal(after.Rois, editor.State.Rois);
    }
    [Fact] public void BoundaryConstraintPreservesDirectionAndCancelOrZeroMovePreservesRedo()
    {
        var editor = new EditorSession(); editor.InitializeImage(new("image", "image", 100, 100), ViewportTransform.Identity);
        var id = editor.Add(new(new(70, 50), 40, 20, 0)); editor.Rename(id, "changed"); editor.Undo();
        var before = editor.State;
        editor.BeginPreview(); editor.PreviewTranslation(new(40, 20));
        Assert.Equal(new PointD(10, 5), editor.TranslationPreview!.Delta);
        editor.CancelPreview(); Assert.Same(before, editor.State); Assert.True(editor.CanRedo); Assert.Null(editor.TranslationPreview);
        editor.BeginPreview(); editor.PreviewTranslation(new(10, 5)); editor.PreviewTranslation(default); editor.CommitPreview("이동");
        Assert.Same(before.Rois, editor.State.Rois); Assert.True(editor.CanRedo);
        editor.BeginPreview(); editor.PreviewTranslation(new(40, 20)); editor.CommitPreview("이동");
        Assert.True(RoiImageBounds.ContainsAll(editor.State)); Assert.False(editor.CanRedo);
    }
    [Fact] public void DensePreviewDoesNotAllocateOneGeometryPerPointerSample()
    {
        var editor = new EditorSession(); editor.AddRange(Enumerable.Range(0, 10000).Select(i => new RotatedRect(new(i * 20, 100), 10, 10, 0)));
        editor.Select(editor.State.Rois.Keys); var before = editor.State;
        editor.BeginPreview(); editor.PreviewTranslation(new(1, 1));
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 2; i < 102; i++) editor.PreviewTranslation(new(i, i));
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - allocated, 0, 32 * 1024);
        Assert.Same(before, editor.State); Assert.Same(before.Snapshot.Spatial, editor.TranslationPreview!.Selection.Moving);
        Assert.Empty(editor.TranslationPreview.Selection.Stationary.Intersecting(before.Snapshot.Spatial.Bounds));
        editor.CancelPreview();
    }
}
