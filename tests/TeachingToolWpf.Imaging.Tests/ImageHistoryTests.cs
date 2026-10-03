using TeachingToolWpf.Core.Editing;
using TeachingToolWpf.Core.Geometry;
using TeachingToolWpf.Core.Imaging;
using TeachingToolWpf.Imaging;
using TeachingToolWpf.Presentation;
using TeachingToolWpf.Rendering;

namespace TeachingToolWpf.Imaging.Tests;

public class ImageHistoryTests
{
    [Fact]
    public async Task CancellationAfterSourceCloseBeginsCompletesPreparedReplacement()
    {
        var editor = new EditorSession();
        using var view = new Viewport();
        var closing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stops = 0;
        using var images = new ImageCoordinator(editor, view, new Loader(), async () =>
        {
            if (++stops == 2)
            {
                closing.SetResult();
                await closed.Task;
            }
        });
        await images.ReplaceAsync(new("sample:100:100", "A", 100, 100));
        var replacement = images.ReplaceAsync(new("sample:200:200", "B", 200, 200));
        await closing.Task;
        images.Cancel();
        closed.SetResult();
        await replacement;
        Assert.Equal(200, editor.State.Image!.Width);
        Assert.Equal(200, images.Image!.Width);
        Assert.Equal(2, images.Generation);
        Assert.True(editor.CanUndo);
    }

    [Fact]
    public async Task FailedReplacementDoesNotStopCurrentSourceOrClearRois()
    {
        var editor = new EditorSession();
        using var view = new Viewport();
        var loader = new Loader();
        var stops = 0;
        using var images = new ImageCoordinator(editor, view, loader, () =>
        {
            stops++;
            return Task.CompletedTask;
        });
        await images.ReplaceAsync(new("sample:100:100", "A", 100, 100));
        editor.Add(new(new(50, 50), 30, 30, 0));
        var before = editor.State;
        loader.Fail = true;
        await Assert.ThrowsAsync<IOException>(() => images.ReplaceAsync(new("missing", "B", 500, 500)));
        Assert.Same(before, editor.State);
        Assert.Equal(1, stops);
        Assert.Equal(100, images.Image!.Width);
    }

    [Fact]
    public async Task ReplacementClearsRoisAndUndoRestoresSelectionWithSource()
    {
        var editor = new EditorSession();
        using var view = new Viewport();
        using var images = new ImageCoordinator(editor, view, new Loader());
        await images.ReplaceAsync(new("sample:8192:8192", "A", 8192, 8192));
        var id = editor.Add(new(new(100, 100), 40, 40, 15));
        await images.ReplaceAsync(new("sample:24576:1024", "B", 24576, 1024));
        Assert.Empty(editor.State.Rois);
        Assert.Empty(editor.State.Selection);
        await images.HistoryAsync(false);
        Assert.Equal(8192, images.Image!.Width);
        Assert.Contains(id, editor.State.Selection);
        Assert.Single(editor.State.Rois);
        await images.HistoryAsync(true);
        Assert.Empty(editor.State.Rois);
    }

    private sealed class Viewport : IViewportSurface, IDisposable
    {
        private ITileSource? _source;

        public ITileSource? Source => _source;

        public long SourceTransfers
        {
            get;
            private set;
        }

        public double ViewWidth => 1000;

        public double ViewHeight => 700;

        public double Dpi => 1;

        public event Action? LayoutChanged
        {
            add
            {
            }
            remove
            {
            }
        }

        public long SetSource(ITileSource value)
        {
            _source?.Dispose();
            _source = value;
            return ++SourceTransfers;
        }

        public void Submit(RenderScene scene)
        {
        }

        public Task RestartAsync() => Task.CompletedTask;

        public void Dispose() => _source?.Dispose();
    }
    private sealed class Loader : IImageLoader
    {
        public bool Fail
        {
            get;
            set;
        }

        public Task<ITileSource> LoadAsync(ImageReference reference, IProgress<string> progress, CancellationToken token)
            => Fail ? Task.FromException<ITileSource>(new IOException("missing source")) : Task.FromResult<ITileSource>(new ProceduralTileSource(reference.Width, reference.Height));
    }

    [Fact]
    public async Task FailedUndoLeavesCurrentDocumentAndHistoryUntouched()
    {
        var editor = new EditorSession();
        using var view = new Viewport();
        var loader = new Loader();
        using var images = new ImageCoordinator(editor, view, loader);
        await images.ReplaceAsync(new("sample:8192:8192", "A", 8192, 8192));
        Assert.False(editor.CanUndo);
        await images.ReplaceAsync(new("sample:24576:1024", "B", 24576, 1024));
        var before = editor.State;
        var bytes = editor.HistoryBytes;
        loader.Fail = true;
        await Assert.ThrowsAsync<IOException>(() => images.HistoryAsync(false));
        Assert.Same(before, editor.State);
        Assert.Equal(bytes, editor.HistoryBytes);
        Assert.True(editor.CanUndo);
        Assert.False(editor.CanRedo);
        loader.Fail = false;
        await images.HistoryAsync(false);
        Assert.Equal(8192, editor.State.Image!.Width);
        Assert.True(editor.CanRedo);
        await images.HistoryAsync(true);
        Assert.Equal(24576, editor.State.Image!.Width);
    }

    [Fact]
    public async Task StalePreparedHistoryDisposesReloadWithoutReplacingCurrentSourceOrMovingCursor()
    {
        var editor = new EditorSession();
        using var view = new Viewport();
        var loader = new GatedLoader();
        var stops = 0;
        using var images = new ImageCoordinator(editor, view, loader, () =>
        {
            stops++;
            return Task.CompletedTask;
        });
        await images.ReplaceAsync(new("sample:100:100", "A", 100, 100));
        await images.ReplaceAsync(new("sample:200:200", "B", 200, 200));
        var source = Assert.IsType<TrackedSource>(view.Source);
        var info = images.Image;
        var generation = images.Generation;
        loader.HoldNextLoad = true;
        var undo = images.HistoryAsync(false);
        await loader.Started.Task;
        var added = editor.Add(new(new(50, 50), 20, 20, 0));
        var current = editor.State;
        var bytes = editor.HistoryBytes;
        var undoName = editor.UndoName;
        loader.Release.SetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => undo);

        Assert.Same(current, editor.State);
        Assert.Same(source, view.Source);
        Assert.Same(info, images.Image);
        Assert.Equal(generation, images.Generation);
        Assert.Equal(generation, view.SourceTransfers);
        Assert.Equal(0, source.DisposeCount);
        Assert.Equal(1, loader.HeldSource!.DisposeCount);
        Assert.Equal(2, stops);
        Assert.Equal(bytes, editor.HistoryBytes);
        Assert.Equal(undoName, editor.UndoName);
        Assert.True(editor.CanUndo);
        Assert.False(editor.CanRedo);
        Assert.False(images.IsBusy);
        editor.Undo();
        Assert.DoesNotContain(added, editor.State.Rois.Keys);
        Assert.Equal("sample:200:200", editor.State.Image!.Key);
    }

    [Fact]
    public async Task CancellingPreparedHistoryDisposesLateReloadAndKeepsOriginalSourceAndCursor()
    {
        var editor = new EditorSession();
        using var view = new Viewport();
        var loader = new GatedLoader();
        var stops = 0;
        using var images = new ImageCoordinator(editor, view, loader, () =>
        {
            stops++;
            return Task.CompletedTask;
        });
        await images.ReplaceAsync(new("sample:100:100", "A", 100, 100));
        await images.ReplaceAsync(new("sample:200:200", "B", 200, 200));
        var current = editor.State;
        var bytes = editor.HistoryBytes;
        var undoName = editor.UndoName;
        var source = Assert.IsType<TrackedSource>(view.Source);
        var info = images.Image;
        var generation = images.Generation;
        loader.HoldNextLoad = true;
        var undo = images.HistoryAsync(false);
        await loader.Started.Task;
        images.Cancel();
        loader.Release.SetResult();
        await undo;

        Assert.Same(current, editor.State);
        Assert.Same(source, view.Source);
        Assert.Same(info, images.Image);
        Assert.Equal(generation, images.Generation);
        Assert.Equal(generation, view.SourceTransfers);
        Assert.Equal(0, source.DisposeCount);
        Assert.Equal(1, loader.HeldSource!.DisposeCount);
        Assert.Equal(2, stops);
        Assert.Equal(bytes, editor.HistoryBytes);
        Assert.Equal(undoName, editor.UndoName);
        Assert.True(editor.CanUndo);
        Assert.False(editor.CanRedo);
        Assert.False(images.IsBusy);
        await images.HistoryAsync(false);
        Assert.Equal("sample:100:100", editor.State.Image!.Key);
        Assert.True(editor.CanRedo);
    }

    [Fact]
    public async Task RoiOnlyPreparedHistoryPreservesPanMadeWhilePublicationIsPending()
    {
        var editor = new EditorSession();
        using var view = new Viewport();
        var loader = new GatedLoader();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var images = new ImageCoordinator(editor, view, loader, preparePublication: async (_, token) =>
        {
            started.SetResult();
            await release.Task.WaitAsync(token);
        });
        await images.ReplaceAsync(new("sample:200:200", "A", 200, 200));
        var id = editor.Add(new(new(50, 50), 20, 20, 0));
        var original = editor.State.Rois[id].Geometry;
        editor.TranslateSelection(new(10, 20));
        var source = view.Source;
        var info = images.Image;
        var generation = images.Generation;
        var undo = images.HistoryAsync(false);
        await started.Task;
        var panned = editor.State.Viewport.Pan(new PointD(70, -40));
        editor.SetViewport(panned);
        release.SetResult();
        await undo;

        Assert.Equal(original, editor.State.Rois[id].Geometry);
        Assert.Equal(panned, editor.State.Viewport);
        Assert.True(editor.State.Selection.SetEquals([id]));
        Assert.True(editor.CanRedo);
        Assert.Same(source, view.Source);
        Assert.Same(info, images.Image);
        Assert.Equal(generation, images.Generation);
        Assert.Equal(generation, view.SourceTransfers);
        Assert.False(images.IsBusy);
    }

    private sealed class GatedLoader : IImageLoader
    {
        public bool HoldNextLoad
        {
            get;
            set;
        }

        public TrackedSource? HeldSource
        {
            get;
            private set;
        }

        public TaskCompletionSource Started
        {
            get;
        } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release
        {
            get;
        } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ITileSource> LoadAsync(ImageReference reference, IProgress<string> progress, CancellationToken token)
        {
            var source = new TrackedSource(reference.Width, reference.Height);
            if (HoldNextLoad)
            {
                HoldNextLoad = false;
                HeldSource = source;
                Started.SetResult();
                // Model a native load that finishes after cancellation: the coordinator owns the returned source.
                await Release.Task;
            }
            return source;
        }
    }
    private sealed class TrackedSource(int width, int height) : ITileSource
    {
        private readonly ProceduralTileSource _source = new(width, height);

        public int DisposeCount
        {
            get;
            private set;
        }

        public ImageInfo Info => _source.Info;

        public PixelBuffer Read(TileKey key, CancellationToken token) => _source.Read(key, token);

        public void Dispose()
        {
            DisposeCount++;
            _source.Dispose();
        }
    }
}
