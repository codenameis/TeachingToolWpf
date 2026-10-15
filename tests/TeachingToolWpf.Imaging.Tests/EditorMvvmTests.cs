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
    public void DoubleClickSelectsALargeGroupImmediatelyWithoutPreparingItsDescendants()
    {
        using var display = new Display();
        using var model = Create(display, new Loader());
        model.Session.InitializeImage(new("test", "test", 1000, 1000), new(1, default));
        model.Session.AddRange(new[] { new RoiCreation(new(new(500, 500), 800, 800, 0)) }
            .Concat(Enumerable.Range(0, 5000).Select(i => new RoiCreation(
                new(new(200 + (i % 100 * 5), 200 + (i / 100 * 5)), 2, 2, 0), 0))));
        var rois = model.Session.State.Rois.Values.OrderBy(roi => roi.Order).ToArray();
        model.Session.Select([rois[1].Id]);
        var before = model.Session.State.Rois;
        var bytes = model.Session.HistoryBytes;
        model.Input.DoubleClick(rois[1].Geometry.Center);
        Assert.True(model.CanEdit);
        Assert.True(model.EditCompletion.IsCompletedSuccessfully);
        Assert.Equal(rois[0].Id, model.Session.State.Selection.Single());
        Assert.False(model.Session.State.IsTransformSelectionPrepared);
        Assert.Same(before, model.Session.State.Rois);
        Assert.Equal(bytes, model.Session.HistoryBytes);
    }

    [Fact]
    public void RotationCommandsAreAvailableOnlyForASingleRoiOrGroup()
    {
        using var display = new Display();
        using var model = Create(display, new Loader());
        model.Session.InitializeImage(new("test", "test", 1000, 1000), new(1, default));
        var parent = model.Session.AddParent(new(new(500, 500), 400, 400, 0));
        var child = model.Session.AddChild(parent, new(new(500, 500), 20, 20, 0));
        foreach (var id in new[] { parent, child })
        {
            model.Session.Select([id]);
            Assert.True(model.RotateLeft.CanExecute(null));
            Assert.True(model.RotateRight.CanExecute(null));
            Assert.True(model.ResetAngle.CanExecute(null));
        }
        model.Session.Select([parent, child]);
        Assert.False(model.RotateLeft.CanExecute(null));
        Assert.False(model.RotateRight.CanExecute(null));
        Assert.False(model.ResetAngle.CanExecute(null));
    }

    [Fact]
    public async Task InitializationCommandLoads8KWithoutAWindow()
    {
        using var display = new Display();
        var loader = new Loader();
        using var model = Create(display, loader);
        await model.Initialize.ExecuteAsync();
        Assert.Equal("sample:8192:8192", loader.Request!.Key);
        Assert.Equal(8192, loader.Request.Width);
        Assert.Equal(8192, loader.Request.Height);
        Assert.Same(loader.Source, display.Source);
        Assert.Equal(loader.Request.Key, model.Session.State.Image!.Key);
        Assert.NotNull(model.Scene);
    }

    [Fact]
    public async Task InitializationCommandReportsFailureAndCanRetryWithoutAViewHandler()
    {
        using var display = new Display();
        var loader = new Loader { Failure = new InvalidOperationException("이미지 준비 실패") };
        using var model = Create(display, loader);
        var before = model.Session.State;
        // WPF invokes ICommand.Execute, which must report the error without a View catch block.
        model.Initialize.Execute(null);
        Assert.Equal("이미지 준비 실패", model.Status);
        Assert.Same(before, model.Session.State);
        Assert.Null(display.Source);
        Assert.True(model.Initialize.CanExecute(null));
        loader.Failure = null;
        await model.Initialize.ExecuteAsync();
        Assert.NotNull(model.Scene);
        Assert.Same(loader.Source, display.Source);
    }

    [Theory]
    [InlineData(2.5, "GPU 2.5 ms")]
    [InlineData(null, "장치 시간 미측정")]
    public void PerformanceStateDistinguishesMeasuredGpuTimeFromUnavailableTime(double? gpu, string expected)
    {
        using var display = new Display();
        using var model = Create(display, new Loader());
        var before = model.Session.State;
        var changes = new List<string?>();
        model.PropertyChanged += (_, args) => changes.Add(args.PropertyName);
        model.UpdatePerformance(new(1, gpu, 0, 16, null, 7, 1, 0, 0));
        Assert.Contains(expected.Replace("2.5", 2.5.ToString("F1")), model.Performance);
        Assert.Contains("표시 드롭 7", model.Performance);
        Assert.Contains(nameof(model.Performance), changes);
        Assert.Same(before, model.Session.State);
    }

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
        model.UpdateRendererStatus("test adapter");
        model.UpdatePerformance(new(1, null, 0, 16, null, 0, 1, 0, 0));
        Assert.Equal("test adapter", model.Adapter);
        Assert.Contains("미측정 (WARP)", model.Performance);
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

    [Fact]
    public async Task RepeatedInitializationReportsFailureAndAllowsRetry()
    {
        using var display = new Display();
        var loader = new Loader { Failure = new InvalidOperationException("원본 읽기 실패") };
        using var model = new RepeatedRoiViewModel(display, new(1000, 700), new("test", "test", 1000, 1000));
        await Assert.ThrowsAsync<InvalidOperationException>(() => model.InitializeAsync(loader));
        Assert.Equal("원본 읽기 실패", model.Status);
        Assert.Null(display.Source);
        loader.Failure = null;
        await model.InitializeAsync(loader);
        Assert.Same(loader.Source, display.Source);
    }

    [Fact]
    public async Task RepeatedEditorOwnsLoadingCancellationWithoutAWindow()
    {
        using var display = new Display();
        using var model = new RepeatedRoiViewModel(display, new(1000, 700), new("test", "test", 1000, 1000));
        var pending = model.InitializeAsync(new PendingLoader());
        model.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal("읽기 취소", model.Status);
        Assert.Null(display.Source);
    }

    private sealed class PendingLoader : IImageLoader
    {
        public async Task<ITileSource> LoadAsync(ImageReference reference, IProgress<string> progress, CancellationToken token)
        {
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("The pending load must be cancelled.");
        }
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
        public Exception? Failure
        {
            get; set;
        }
        public ImageReference? Request
        {
            get; private set;
        }
        public Source Source { get; } = new();
        public Task<ITileSource> LoadAsync(ImageReference reference, IProgress<string> progress, CancellationToken token)
        {
            Request = reference;
            return Failure is { } failure ? Task.FromException<ITileSource>(failure) : Task.FromResult<ITileSource>(Source);
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
