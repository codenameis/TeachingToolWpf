using TeachingToolWpf.Core.Editing;
using TeachingToolWpf.Core.Imaging;
using TeachingToolWpf.Imaging;
using TeachingToolWpf.Presentation;
using TeachingToolWpf.Rendering;

namespace TeachingToolWpf.Imaging.Tests;

public class ImageHistoryTests
{
    [Fact] public async Task FailedReplacementDoesNotStopCurrentSourceOrClearRois()
    {
        var editor = new EditorSession(); using var view = new Viewport(); var loader = new Loader(); var stops = 0;
        using var images = new ImageCoordinator(editor, view, loader, () => { stops++; return Task.CompletedTask; });
        await images.ReplaceAsync(new("sample:100:100", "A", 100, 100));
        editor.Add(new(new(50, 50), 30, 30, 0)); var before = editor.State;
        loader.Fail = true;
        await Assert.ThrowsAsync<IOException>(() => images.ReplaceAsync(new("missing", "B", 500, 500)));
        Assert.Same(before, editor.State); Assert.Equal(1, stops); Assert.Equal(100, images.Image!.Width);
    }
    [Fact] public async Task ReplacementClearsRoisAndUndoRestoresSelectionWithSource()
    {
        var editor = new EditorSession(); using var view = new Viewport();
        using var images = new ImageCoordinator(editor, view, new Loader());
        await images.ReplaceAsync(new("sample:8192:8192", "A", 8192, 8192));
        var id = editor.Add(new(new(100, 100), 40, 40, 15));
        await images.ReplaceAsync(new("sample:24576:1024", "B", 24576, 1024));
        Assert.Empty(editor.State.Rois); Assert.Empty(editor.State.Selection);
        await images.HistoryAsync(false); Assert.Equal(8192, images.Image!.Width); Assert.Contains(id, editor.State.Selection);
        Assert.Single(editor.State.Rois); await images.HistoryAsync(true); Assert.Empty(editor.State.Rois);
    }
    private sealed class Viewport : IViewportSurface, IDisposable
    {
        private ITileSource? source;
        public double ViewWidth => 1000; public double ViewHeight => 700; public double Dpi => 1;
        public event Action? LayoutChanged { add { } remove { } }
        public long SetSource(ITileSource value) { source?.Dispose(); source = value; return 1; }
        public void Submit(RenderScene scene) { }
        public Task RestartAsync() => Task.CompletedTask;
        public void Dispose() => source?.Dispose();
    }
    private sealed class Loader : IImageLoader
    {
        public bool Fail { get; set; }
        public Task<ITileSource> LoadAsync(ImageReference reference, IProgress<string> progress, CancellationToken token)
            => Fail ? Task.FromException<ITileSource>(new IOException("missing source")) : Task.FromResult<ITileSource>(new ProceduralTileSource(reference.Width, reference.Height));
    }
    [Fact] public async Task FailedUndoLeavesCurrentDocumentAndHistoryUntouched()
    {
        var editor = new EditorSession(); using var view = new Viewport(); var loader = new Loader();
        using var images = new ImageCoordinator(editor, view, loader);
        await images.ReplaceAsync(new("sample:8192:8192", "A", 8192, 8192));
        Assert.False(editor.CanUndo);
        await images.ReplaceAsync(new("sample:24576:1024", "B", 24576, 1024));
        var before = editor.State; var bytes = editor.HistoryBytes;
        loader.Fail = true;
        await Assert.ThrowsAsync<IOException>(() => images.HistoryAsync(false));
        Assert.Same(before, editor.State); Assert.Equal(bytes, editor.HistoryBytes); Assert.True(editor.CanUndo); Assert.False(editor.CanRedo);
        loader.Fail = false;
        await images.HistoryAsync(false); Assert.Equal(8192, editor.State.Image!.Width); Assert.True(editor.CanRedo);
        await images.HistoryAsync(true); Assert.Equal(24576, editor.State.Image!.Width);
    }
}
