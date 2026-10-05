using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Interaction;
using VisionGeometry;

namespace TeachingToolWpf.Core.Tests;

public class PreparedEditingTests
{
    private static EditorSession Create()
    {
        var editor = new EditorSession();
        editor.InitializeImage(new("image", "image", 2048, 2048), ViewportTransform.Identity);
        return editor;
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("select")]
    [InlineData("translate")]
    [InlineData("preview")]
    public async Task CancelledPreparationPreservesDocumentAndRedo(string operation)
    {
        var editor = Create();
        var id = editor.Add(new(new(200, 200), 40, 40, 0));
        editor.Rename(id, "changed");
        editor.Undo();
        var before = editor.State;
        var bytes = editor.HistoryBytes;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        if (operation == "preview")
        {
            editor.BeginPreview();
            editor.PreviewTranslation(new(20, 10));
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation switch
        {
            "delete" => editor.PrepareDeleteAsync(cancellation.Token),
            "select" => editor.PrepareSelectAllAsync(cancellation.Token),
            "translate" => editor.PrepareTranslationAsync(new(20, 10), cancellation.Token),
            _ => editor.PreparePreviewAsync("이동", cancellation.Token)
        });
        editor.CancelPreview();
        Assert.Same(before, editor.State);
        Assert.Equal(bytes, editor.HistoryBytes);
        Assert.True(editor.CanRedo);
    }

    [Fact]
    public async Task PreparationDoesNotPublishAndCommitPreservesNavigation()
    {
        var editor = Create();
        var id = editor.Add(new(new(200, 200), 40, 40, 0));
        var before = editor.State;
        var bytes = editor.HistoryBytes;
        var pending = editor.PrepareTranslationAsync(new(35, 20));
        var view = before.Viewport.Pan(new(90, -40)).ZoomAt(new(300, 200), 1.2);
        editor.SetViewport(view);
        var prepared = await pending;

        Assert.Same(before.Rois, editor.State.Rois);
        Assert.Equal(bytes, editor.HistoryBytes);
        editor.CommitPrepared(prepared);
        Assert.Equal(view, editor.State.Viewport);
        Assert.Equal(before.Rois[id].Geometry.Translate(new(35, 20)), editor.State.Rois[id].Geometry);
        editor.Undo();
        Assert.Equal(before.Rois[id], editor.State.Rois[id]);
        Assert.Equal(view, editor.State.Viewport);
    }

    [Theory]
    [InlineData("rename")]
    [InlineData("selection")]
    [InlineData("source")]
    [InlineData("undo-redo")]
    public async Task MutationAndUndoRedoRoundTripRejectStalePreparation(string mutation)
    {
        var editor = Create();
        var id = editor.Add(new(new(200, 200), 40, 40, 0));
        editor.Rename(id, "before");
        var before = editor.State;
        var pending = editor.PrepareTranslationAsync(new(20, 10));
        switch (mutation)
        {
            case "rename":
                editor.Rename(id, "newer");
                break;
            case "selection":
                editor.Select([]);
                break;
            case "source":
                editor.SetImage(new("new", "new", 2048, 2048), ViewportTransform.Identity);
                break;
            case "undo-redo":
                editor.Undo();
                editor.Redo();
                Assert.Equal(before.Rois[id], editor.State.Rois[id]);
                Assert.True(before.Selection.SetEquals(editor.State.Selection));
                break;
        }
        var prepared = await pending;
        var current = editor.State;
        var bytes = editor.HistoryBytes;
        Assert.Throws<InvalidOperationException>(() => editor.CommitPrepared(prepared));
        Assert.Same(current, editor.State);
        Assert.Equal(bytes, editor.HistoryBytes);
    }

    [Fact]
    public async Task PreparedChangeBelongsToOneSessionAndCanBeConsumedOnlyOnce()
    {
        var editor = Create();
        editor.Add(new(new(200, 200), 40, 40, 0));
        var prepared = await editor.PrepareTranslationAsync(new(20, 10));
        var other = Create();
        var otherState = other.State;
        Assert.Throws<InvalidOperationException>(() => other.CommitPrepared(prepared));
        Assert.Same(otherState, other.State);
        editor.CommitPrepared(prepared);
        var after = editor.State;
        var bytes = editor.HistoryBytes;
        Assert.Throws<InvalidOperationException>(() => editor.CommitPrepared(prepared));
        Assert.Same(after, editor.State);
        Assert.Equal(bytes, editor.HistoryBytes);
    }

    [Fact]
    public async Task CancelledPreviewInvalidatesPreparedEpochAndPreservesNewPreviewAndView()
    {
        var editor = Create();
        editor.Add(new(new(200, 200), 40, 40, 0));
        var before = editor.State;
        editor.BeginPreview();
        editor.PreviewTranslation(new(20, 10));
        var pending = editor.PreparePreviewAsync("이동");
        var view = before.Viewport.Pan(new(80, 20));
        editor.SetViewport(view);
        editor.CancelPreview();
        Assert.Same(before.Rois, editor.State.Rois);
        Assert.Equal(view, editor.State.Viewport);
        editor.BeginPreview();
        editor.PreviewTranslation(new(5, 5));
        var preview = editor.TransformPreview;
        var prepared = await pending;
        Assert.Throws<InvalidOperationException>(() => editor.CommitPrepared(prepared));
        Assert.Same(preview, editor.TransformPreview);
        Assert.True(editor.IsPreviewing);
        editor.CancelPreview();
    }

    [Fact]
    public async Task ParentAndSelectedChildMoveOnceWithoutAutomaticReparenting()
    {
        var editor = Create();
        var parent = editor.Add(new(new(200, 300), 200, 200, 10));
        var child = editor.AddChild(parent, new(new(210, 300), 30, 20, 15));
        var destination = editor.Add(new(new(800, 300), 500, 500, 0));
        var other = editor.Add(new(new(1500, 1200), 20, 20, 0));
        editor.Select([parent, child]);
        var before = editor.State;
        editor.BeginPreview();
        editor.PreviewTranslation(new(600, 0));
        var pending = editor.PreparePreviewAsync("이동");
        var view = before.Viewport.Pan(new(-80, 30));
        editor.SetViewport(view);
        var prepared = await pending;
        Assert.Same(before.Rois, editor.State.Rois);
        editor.CommitPrepared(prepared);

        Assert.Null(editor.TransformPreview);
        Assert.False(editor.IsPreviewing);
        Assert.Equal(view, editor.State.Viewport);
        Assert.Equal(before.Rois[parent].Geometry.Translate(new(600, 0)), editor.State.Rois[parent].Geometry);
        Assert.Equal(before.Rois[child].Geometry.Translate(new(600, 0)), editor.State.Rois[child].Geometry);
        Assert.Null(editor.State.Rois[parent].ParentId);
        Assert.Equal(parent, editor.State.Rois[child].ParentId);
        Assert.Equal(before.Rois[other], editor.State.Rois[other]);
        var after = editor.State;
        editor.Undo();
        AssertDocumentValues(before, editor.State);
        editor.Redo();
        AssertDocumentValues(after, editor.State);
    }

    [Fact]
    public async Task SubtreeDeleteRestoresEveryValueAndSelectionWithOneUndo()
    {
        var editor = Create();
        var child = editor.Add(new(new(300, 300), 100, 100, 0));
        var grandchild = editor.AddChild(child, new(new(300, 300), 20, 20, 25));
        editor.SetFilters(new(RoiFilterKind.Edge));
        var sibling = editor.Add(new(new(500, 300), 40, 40, 0));
        editor.Select([child, sibling]);
        var parent = editor.GroupSelection();
        editor.Rename(parent, "검사 그룹");
        var unrelated = editor.Add(new(new(1500, 1200), 20, 20, 0));
        editor.Select([parent, grandchild]);
        var before = editor.State;

        var prepared = await editor.PrepareDeleteAsync();
        Assert.Same(before, editor.State);
        editor.CommitPrepared(prepared);
        Assert.Equal(unrelated, Assert.Single(editor.State.Rois).Key);
        Assert.Empty(editor.State.Selection);
        var deleted = editor.State;
        editor.Undo();
        AssertDocumentValues(before, editor.State);
        editor.Redo();
        AssertDocumentValues(deleted, editor.State);
    }

    [Fact]
    public async Task NoOpEditsAndSelectAllKeepRedoBranch()
    {
        var editor = Create();
        var first = editor.Add(new(new(200, 200), 40, 40, 0));
        editor.Add(new(new(500, 200), 40, 40, 0));
        editor.Rename(first, "changed");
        editor.Undo();
        var bytes = editor.HistoryBytes;
        editor.CommitPrepared(await editor.PrepareTranslationAsync(default));
        Assert.True(editor.CanRedo);
        Assert.Equal(bytes, editor.HistoryBytes);
        editor.Select([]);
        editor.CommitPrepared(await editor.PrepareDeleteAsync());
        Assert.True(editor.CanRedo);
        Assert.Equal(bytes, editor.HistoryBytes);
        editor.CommitPrepared(await editor.PrepareSelectAllAsync());
        Assert.Equal(2, editor.State.Selection.Count);
        Assert.True(editor.CanRedo);
        editor.CommitPrepared(await editor.PrepareSelectAllAsync());
        Assert.Equal(bytes, editor.HistoryBytes);
        editor.Redo();
        Assert.Equal("changed", editor.State.Rois[first].Name);
    }

    [Fact]
    public void NavigationOnlyBlocksSelectionAndDrawingButAllowsPanMinimapAndWheel()
    {
        var editor = Create();
        var first = editor.Add(new(new(200, 200), 40, 40, 0));
        editor.Add(new(new(400, 200), 40, 40, 0));
        editor.Select([first]);
        var before = editor.State;
        var bytes = editor.HistoryBytes;
        var input = new InteractionController(editor) { NavigationOnly = true };
        input.SetViewportSize(1000, 600);
        input.Down(new(400, 200));
        input.Move(new(450, 240));
        input.Up(new(450, 240));
        Assert.Same(before.Rois, editor.State.Rois);
        Assert.Same(before.Selection, editor.State.Selection);
        input.Tool = EditorTool.Draw;
        input.Down(new(500, 300));
        input.Up(new(600, 400));
        Assert.Same(before.Rois, editor.State.Rois);
        Assert.False(editor.IsPreviewing);

        input.Down(new(100, 100), middle: true);
        input.Up(new(120, 130));
        Assert.Equal(new PointD(20, 30), editor.State.Viewport.Offset);
        input.Tool = EditorTool.Pan;
        input.Down(new(100, 100));
        input.Up(new(140, 120));
        Assert.Equal(new PointD(60, 50), editor.State.Viewport.Offset);
        var previousScale = editor.State.Viewport.Scale;
        input.Wheel(new(300, 200), 1);
        Assert.True(editor.State.Viewport.Scale > previousScale);
        input.Tool = EditorTool.Select;
        var target = input.Minimap!.ImageToMap(new(1200, 1100));
        input.Down(target);
        input.Up(target);
        Assert.True((editor.State.Viewport.ScreenToImage(new(500, 300)) - new PointD(1200, 1100)).Length < 1e-7);
        Assert.Same(before.Rois, editor.State.Rois);
        Assert.Same(before.Selection, editor.State.Selection);
        Assert.Equal(bytes, editor.HistoryBytes);
    }

    private static void AssertDocumentValues(DocumentState expected, DocumentState actual)
    {
        Assert.Equal(expected.Rois.Count, actual.Rois.Count);
        foreach (var pair in expected.Rois)
        {
            Assert.Equal(pair.Value, actual.Rois[pair.Key]);
        }

        Assert.True(expected.Selection.SetEquals(actual.Selection));
        Assert.Equal(expected.Image, actual.Image);
    }
}
