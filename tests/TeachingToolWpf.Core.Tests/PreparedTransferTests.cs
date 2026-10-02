using TeachingToolWpf.Core.Editing;
using TeachingToolWpf.Core.Geometry;

namespace TeachingToolWpf.Core.Tests;

public class PreparedTransferTests
{
    private static EditorSession Create()
    {
        var editor = new EditorSession();
        editor.InitializeImage(new("image", "image", 2048, 2048), ViewportTransform.Identity);
        return editor;
    }

    [Fact] public async Task PreparedPasteKeepsCapturedOffsetFreshIdsOrderAndLatestView()
    {
        var editor = Create();
        editor.Add(new(new(200, 200), 40, 30, 15));
        editor.Add(new(new(600, 200), 50, 25, 25));
        editor.Select(editor.State.Rois.Keys); editor.SetFilters(new(RoiFilterKind.Edge));
        editor.SetViewport(new(2, new(0, 0)));
        var clipboard = new RoiClipboard(); clipboard.Copy(editor.State);
        var before = editor.State; var originals = before.Rois.Values.OrderBy(r => r.Order).ToArray();
        var pending = editor.PreparePasteAsync(clipboard);
        var view = before.Viewport.Pan(new(35, -60)).ZoomAt(new(200, 200), 2);
        editor.SetViewport(view);
        var prepared = await pending;
        Assert.Same(before.Rois, editor.State.Rois); Assert.Equal(1, clipboard.Capture().PasteNumber);

        editor.CommitPreparedPaste(prepared);
        var after = editor.State;
        var copies = after.Rois.Values.Where(r => !before.Rois.ContainsKey(r.Id)).OrderBy(r => r.Order).ToArray();
        Assert.Equal(2, copies.Length); Assert.Equal(view, after.Viewport);
        Assert.True(after.Selection.SetEquals(copies.Select(r => r.Id)));
        for (var i = 0; i < copies.Length; i++)
        {
            Assert.True((copies[i].Geometry.Center - originals[i].Geometry.Center - new PointD(10, 10)).Length < 1e-7);
            Assert.Equal(originals[i].Geometry.Width, copies[i].Geometry.Width);
            Assert.Equal(originals[i].Geometry.Height, copies[i].Geometry.Height);
            Assert.Equal(originals[i].Geometry.Angle, copies[i].Geometry.Angle);
            Assert.Equal(originals[^1].Order + i + 1, copies[i].Order);
            Assert.Equal(originals[i].Filter, copies[i].Filter);
            Assert.Equal(originals[i].Name + " 복사", copies[i].Name);
        }
        Assert.Equal(2, clipboard.Capture().PasteNumber);
        var bytes = editor.HistoryBytes;
        Assert.Throws<InvalidOperationException>(() => editor.CommitPreparedPaste(prepared));
        Assert.Same(after, editor.State); Assert.Equal(bytes, editor.HistoryBytes);
        Assert.Equal(2, clipboard.Capture().PasteNumber);

        editor.Undo(); AssertValues(before, editor.State); Assert.Equal(view, editor.State.Viewport);
        editor.Redo(); AssertValues(after, editor.State); Assert.Equal(view, editor.State.Viewport);
        var added = editor.Add(new(new(1000, 1000), 20, 20, 0));
        Assert.Equal(copies[^1].Order + 1, editor.State.Rois[added].Order);
    }

    [Fact] public async Task RecopyFromAnotherDocumentRejectsPreparedPasteWithoutAdvancingClipboard()
    {
        var editor = Create(); editor.Add(new(new(200, 200), 40, 40, 0));
        var clipboard = new RoiClipboard(); clipboard.Copy(editor.State);
        var before = editor.State; var bytes = editor.HistoryBytes;
        var pending = editor.PreparePasteAsync(clipboard);
        var other = Create(); var replacement = other.Add(new(new(600, 600), 20, 20, 30));
        clipboard.Copy(other.State);
        var prepared = await pending;

        Assert.Throws<InvalidOperationException>(() => editor.CommitPreparedPaste(prepared));
        Assert.Same(before, editor.State); Assert.Equal(bytes, editor.HistoryBytes);
        Assert.Equal(replacement, Assert.Single(clipboard.Capture().Entries).Id);
        Assert.Equal(1, clipboard.Capture().PasteNumber);
    }

    [Fact] public async Task DocumentMutationRejectsPreparedPasteWithoutConsumingClipboard()
    {
        var editor = Create(); var id = editor.Add(new(new(200, 200), 40, 40, 0));
        var clipboard = new RoiClipboard(); clipboard.Copy(editor.State);
        var pending = editor.PreparePasteAsync(clipboard);
        editor.Rename(id, "newer");
        var current = editor.State; var bytes = editor.HistoryBytes;
        var prepared = await pending;
        Assert.Throws<InvalidOperationException>(() => editor.CommitPreparedPaste(prepared));
        Assert.Same(current, editor.State); Assert.Equal(bytes, editor.HistoryBytes);
        Assert.Equal(1, clipboard.Capture().PasteNumber);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledPasteOrHistoryPreparationLeavesStateAndCursorIntact(bool history)
    {
        var editor = Create(); var id = editor.Add(new(new(200, 200), 40, 40, 0));
        editor.Rename(id, "renamed");
        var clipboard = new RoiClipboard(); clipboard.Copy(editor.State);
        var before = editor.State; var bytes = editor.HistoryBytes; var undoName = editor.UndoName;
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        if (history)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => editor.PrepareHistoryAsync(false, cancellation.Token));
        else
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => editor.PreparePasteAsync(clipboard, cancellation.Token));
        Assert.Same(before, editor.State); Assert.Equal(bytes, editor.HistoryBytes);
        Assert.Equal(undoName, editor.UndoName); Assert.False(editor.CanRedo);
        Assert.Equal(1, clipboard.Capture().PasteNumber);
        editor.Undo(); Assert.NotEqual("renamed", editor.State.Rois[id].Name);
    }

    [Fact] public async Task PreparedHistoryPreservesNavigationAndConsumesEachDirectionOnce()
    {
        var editor = Create(); var id = editor.Add(new(new(200, 200), 40, 40, 0));
        var original = editor.State.Rois[id]; editor.Rename(id, "renamed");
        var pending = editor.PrepareHistoryAsync(false);
        var view = new ViewportTransform(2, new(70, -40)); editor.SetViewport(view);
        var undo = await pending;
        Assert.Equal("renamed", editor.State.Rois[id].Name);
        editor.ValidatePreparedHistory(undo); editor.CommitPreparedHistory(undo);
        Assert.Equal(original, editor.State.Rois[id]); Assert.Equal(view, editor.State.Viewport);
        Assert.True(editor.CanRedo);
        Assert.Throws<InvalidOperationException>(() => editor.CommitPreparedHistory(undo));

        var redoPending = editor.PrepareHistoryAsync(true);
        var nextView = view.Pan(new(-15, 25)); editor.SetViewport(nextView);
        editor.CommitPreparedHistory(await redoPending);
        Assert.Equal("renamed", editor.State.Rois[id].Name); Assert.Equal(nextView, editor.State.Viewport);
        Assert.False(editor.CanRedo);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HistoryCursorChangesRejectOldSessionPreparation(bool roundTrip)
    {
        var editor = Create(); var id = editor.Add(new(new(200, 200), 40, 40, 0));
        editor.Rename(id, "renamed"); var original = editor.State.Rois[id];
        var pending = editor.PrepareHistoryAsync(false);
        editor.Undo();
        if (roundTrip) { editor.Redo(); Assert.Equal(original, editor.State.Rois[id]); }
        var prepared = await pending; var current = editor.State;
        var undoName = editor.UndoName; var redoName = editor.RedoName;
        Assert.Throws<InvalidOperationException>(() => editor.ValidatePreparedHistory(prepared));
        Assert.Throws<InvalidOperationException>(() => editor.CommitPreparedHistory(prepared));
        Assert.Same(current, editor.State);
        Assert.Equal(undoName, editor.UndoName); Assert.Equal(redoName, editor.RedoName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangingPreviewDeltaRejectsPreparedGeometryEvenIfDeltaReturns(bool roundTrip)
    {
        var editor = Create(); editor.Add(new(new(200, 200), 40, 40, 0));
        var before = editor.State; var bytes = editor.HistoryBytes;
        editor.BeginPreview(); editor.PreviewTranslation(new(20, 10));
        var pending = editor.PreparePreviewAsync("이동");
        editor.PreviewTranslation(new(50, 30));
        if (roundTrip) editor.PreviewTranslation(new(20, 10));
        var latest = editor.TransformPreview;
        var prepared = await pending;
        Assert.Throws<InvalidOperationException>(() => editor.CommitPrepared(prepared));
        Assert.Same(before, editor.State); Assert.Same(latest, editor.TransformPreview);
        Assert.True(editor.IsPreviewing); Assert.Equal(bytes, editor.HistoryBytes);
        editor.CancelPreview();
    }

    private static void AssertValues(DocumentState expected, DocumentState actual)
    {
        Assert.Equal(expected.Rois.Count, actual.Rois.Count);
        foreach (var pair in expected.Rois) Assert.Equal(pair.Value, actual.Rois[pair.Key]);
        Assert.True(expected.Selection.SetEquals(actual.Selection)); Assert.Equal(expected.Image, actual.Image);
    }
}
