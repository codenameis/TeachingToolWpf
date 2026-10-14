using System.Windows.Input;
using TeachingToolWpf.ViewModels;
using VisionCanvas.Core.Editing;
using VisionCanvas.Imaging.Filters;
using VisionCanvas.Presentation;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Imaging.Tests;

public class EditorMvvmTests
{
    [Fact]
    public async Task SceneLayoutAndDeviceCommandsWorkWithoutAView()
    {
        using var display = new Display();
        using var model = Create(display, new Loader());
        model.Session.InitializeImage(new("test", "test", 1000, 1000), new(1, default));
        var parent = model.Session.Add(new(new(100, 100), 80, 80, 0));
        model.Session.AddChild(parent, new(new(100, 100), 10, 10, 0));
        var detached = model.Session.Add(new(new(800, 800), 20, 20, 0));
        model.Session.Select([parent, detached]);
        Assert.True(model.Group.CanExecute(null));
        await model.Group.ExecuteAsync();
        Assert.Equal(3, model.Scene!.Document.Rois.Count);
        Assert.Equal(parent, model.Scene.Document.Rois[detached].ParentId);
        await model.Undo.ExecuteAsync();
        Assert.Null(model.Scene!.Document.Rois[detached].ParentId);

        var before = model.Session.State;
        var bytes = model.Session.HistoryBytes;
        model.Layout.Update(640, 480, 2);
        Assert.Equal(640, model.Scene!.Width);
        Assert.Equal(480, model.Scene.Height);
        Assert.Equal(2, model.Scene.Dpi);
        Assert.Equal("200", model.ZoomText);
        Assert.Same(before, model.Session.State);
        Assert.Equal(bytes, model.Session.HistoryBytes);
        await model.RestartRenderer.ExecuteAsync();
        Assert.Equal(1, display.Restarts);
        model.Dispose();
        var lastScene = model.Scene;
        model.Layout.Update(800, 600, 1);
        model.Publish();
        Assert.Same(lastScene, model.Scene);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImagePickerReturnsAPathOrCancelsWithoutOpeningAWindow(bool cancel)
    {
        using var display = new Display();
        var loader = new Loader();
        var picks = 0;
        var path = typeof(EditorMvvmTests).Assembly.Location;
        using var model = Create(display, loader, () => { picks++; return cancel ? null : path; });
        var before = model.Session.State;
        Assert.True(model.OpenImage.CanExecute(null));
        await model.OpenImage.ExecuteAsync();
        Assert.Equal(1, picks);
        if (cancel)
        {
            Assert.Null(loader.Request);
            Assert.Same(before, model.Session.State);
            Assert.Null(display.Source);
        }
        else
        {
            Assert.Equal(path, loader.Request!.Key);
            Assert.Equal(path, model.Session.State.Image!.Key);
            Assert.Same(loader.Source, display.Source);
            Assert.False(loader.Source.Disposed);
            Assert.Equal(1, model.Scene!.Generation);
        }
    }

    [Fact]
    public async Task RepeatedEditorLoadsAndUndoesWithoutAControlAndReleasesCancelledSources()
    {
        using var display = new Display();
        var loader = new Loader();
        using var model = new RepeatedRoiViewModel(display, new(1000, 700), new("test", "test", 1000, 1000));
        await model.LoadImageAsync(loader, CancellationToken.None);
        var grid = model.Scene!.RepeatedGrid;
        model.Move(new(10, 10));
        Assert.NotSame(grid, model.Scene!.RepeatedGrid);
        Assert.True(model.HandleKey(Key.Z, true, false));
        Assert.Same(grid, model.Scene!.RepeatedGrid);
        Assert.True(model.HandleKey(Key.Z, true, true));
        Assert.NotSame(grid, model.Scene!.RepeatedGrid);
        model.Dispose();
        var nextLoader = new Loader();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => model.LoadImageAsync(nextLoader, CancellationToken.None));
        Assert.True(nextLoader.Source.Disposed);
        Assert.False(loader.Source.Disposed);
    }

    private static MainWindowViewModel Create(Display display, Loader loader, Func<string?>? selectImage = null)
        => new(display, new(1000, 700), loader, new(display, _ => throw new NotSupportedException()),
            new UnusedFilter(), EditorTestServices.Inspections(), selectImage);

    // This service cannot submit a scene, observe layout, or access a native control.
    private sealed class Display : IImageDisplay, IFramePresenter, IDisposable
    {
        public ITileSource? Source
        {
            get; private set;
        }
        public int Restarts
        {
            get; private set;
        }
        public long SetSource(ITileSource source)
        {
            Source = source;
            return 1;
        }
        public Task RestartAsync()
        {
            Restarts++;
            return Task.CompletedTask;
        }
        public void PublishFrame(VideoFrame frame) => throw new NotSupportedException();
        public void ClearFrames()
        {
        }
        public void Dispose() => Source?.Dispose();
    }

    private sealed class Loader : IImageLoader
    {
        public ImageReference? Request
        {
            get; private set;
        }
        public Source Source { get; } = new();
        public Task<ITileSource> LoadAsync(ImageReference reference, IProgress<string> progress, CancellationToken token)
        {
            Request = reference;
            return Task.FromResult<ITileSource>(Source);
        }
    }

    private sealed class Source : ITileSource
    {
        public ImageInfo Info { get; } = new("test", "test", 1000, 1000, PixelKind.Gray8, 2);
        public bool Disposed
        {
            get; private set;
        }
        public PixelBuffer Read(TileKey key, CancellationToken cancellationToken) => throw new NotSupportedException();
        public void Dispose() => Disposed = true;
    }

    private sealed class UnusedFilter : IRoiFilterPreview
    {
        public Task<FilterPreviewResult> ProcessAsync(FilterRequest request, CancellationToken token) => throw new NotSupportedException();
    }
}
