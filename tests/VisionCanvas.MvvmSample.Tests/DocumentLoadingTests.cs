using VisionCanvas.Core.Editing;
using VisionCanvas.Presentation;
using VisionGeometry;
using VisionImaging;
using VisionImaging.Abstractions;

namespace VisionCanvas.MvvmSample.Tests;

public sealed class DocumentLoadingTests
{
    [Fact]
    public async Task FirstModelIsBaselineAndReplacementIsOneHistoryStep()
    {
        var editor = new EditorSession();
        using var display = new Display();
        using var images = new ImageCoordinator(editor, display, new(800, 600), new Loader());
        var parent = new Roi(Guid.NewGuid(), "그룹", new(new(400, 400), 500, 500, 0), null, true, 10);
        var child = new Roi(Guid.NewGuid(), "원", new(new(400, 400), 100, 100, 0, RoiShape.Circle), parent.Id, false, 11);
        await images.LoadDocumentAsync(new("sample:first", "첫 모델", 8192, 8192), [parent, child]);
        Assert.False(editor.CanUndo);
        Assert.Equal(parent.Id, editor.State.Rois[child.Id].ParentId);
        var first = editor.State;
        Assert.Equal(ViewportTransform.Fit(8192, 8192, 800, 600), first.Viewport);
        var added = editor.Add(new(new(800, 800), 100, 100, 0));
        Assert.Equal(12, editor.State.Rois[added].Order);
        editor.SetViewport(new(1, new(-100, -200)));
        var previousView = editor.State.Viewport;
        await images.LoadDocumentAsync(new("sample:second", "두 번째 모델", 8192, 8192), []);
        Assert.Empty(editor.State.Rois);
        await images.HistoryAsync(false);
        Assert.Equal(3, editor.State.Rois.Count);
        Assert.Equal(first.Image, editor.State.Image);
        Assert.Equal(previousView, editor.State.Viewport);
        await images.HistoryAsync(true);
        Assert.Empty(editor.State.Rois);
        Assert.Equal(first.Viewport, editor.State.Viewport);
    }

    [Fact]
    public async Task InvalidHierarchyAndWrongImageSizeDoNotTransferSourceOrChangeDocument()
    {
        var editor = new EditorSession();
        using var display = new Display();
        using var images = new ImageCoordinator(editor, display, new(800, 600), new Loader());
        await images.ReplaceAsync(new("sample:first", "원본", 8192, 8192));
        var before = editor.State;
        var roi = new Roi(Guid.NewGuid(), "ROI", new(new(400, 400), 100, 100, 0), null, false, 1);
        await Assert.ThrowsAsync<ArgumentException>(() => images.LoadDocumentAsync(before.Image!, [roi with { ParentId = roi.Id }]));
        await Assert.ThrowsAsync<ArgumentException>(() => images.LoadDocumentAsync(before.Image!, [roi with { ParentId = Guid.NewGuid() }]));
        await Assert.ThrowsAsync<ArgumentException>(() => images.LoadDocumentAsync(before.Image!, [roi with { Geometry = new(new(-100, 400), 100, 100, 0) }]));
        await Assert.ThrowsAsync<ArgumentException>(() => images.LoadDocumentAsync(before.Image!, [roi with { IsGroup = true, SearchRange = new(10, 10) }]));
        await Assert.ThrowsAsync<ArgumentException>(() => images.LoadDocumentAsync(before.Image!, [roi with { Filter = new(RoiFilterKind.Binarize, -1) }]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => images.LoadDocumentAsync(new("sample:bad", "잘못된 크기", 1000, 1000), [roi]));
        Assert.Equal(before, editor.State);
        Assert.Equal(1, display.Transfers);
        Assert.False(editor.CanUndo);
    }

    [Fact]
    public async Task CancelledOrStalePreparedModelCannotOverwriteTheCurrentDocument()
    {
        var editor = new EditorSession();
        var reference = new ImageReference("sample:first", "모델", 8192, 8192);
        var prepared = await editor.PrepareDocumentAsync(reference, []);
        editor.InitializeImage(reference, ViewportTransform.Identity);
        Assert.Throws<InvalidOperationException>(() => editor.ValidatePrepared(prepared));
        Assert.Throws<InvalidOperationException>(() => editor.CommitPrepared(prepared));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => editor.PrepareDocumentAsync(reference, [], token: cancellation.Token));
        Assert.False(editor.CanUndo);
    }

    private sealed class Loader : IImageLoader
    {
        public Task<ITileSource> LoadAsync(ImageReference reference, IProgress<string> progress, CancellationToken cancellationToken)
            => Task.FromResult<ITileSource>(new ProceduralTileSource(8192, 8192));
    }

    private sealed class Display : IImageDisplay, IDisposable
    {
        private ITileSource? _source;
        public int Transfers { get; private set; }
        public long SetSource(ITileSource source)
        {
            _source?.Dispose();
            _source = source;
            return ++Transfers;
        }
        public Task RestartAsync() => Task.CompletedTask;
        public void Dispose() => _source?.Dispose();
    }
}
