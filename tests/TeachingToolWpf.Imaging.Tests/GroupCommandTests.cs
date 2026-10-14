using System.ComponentModel;
using System.Windows.Input;
using System.Windows.Threading;
using TeachingToolWpf.Services;
using TeachingToolWpf.ViewModels;
using VisionCanvas.Core.Editing;
using VisionCanvas.Imaging.Filters;
using VisionCanvas.Presentation;
using VisionCanvas.Rendering;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Imaging.Tests;

public class GroupCommandTests
{
    [Fact]
    public void GroupAndUngroupTrackCompletionPrepareRowsAndPreserveNavigation() => OnStaAsync(async () =>
    {
        using var model = Create();
        var before = model.Session.State;
        var navigations = 0;
        var view = before.Viewport;
        model.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(model.Status) || !model.Status.Contains("준비 중"))
            {
                return;
            }
            Assert.False(model.CanEdit);
            Assert.True(model.CanNavigate);
            model.Session.SetViewport(model.Session.State.Viewport.Pan(new(12, 8)));
            model.Input.Wheel(new(300, 200), .1);
            view = model.Session.State.Viewport;
            navigations++;
        };

        model.Group.Execute(null);
        await model.EditCompletion;
        var grouped = model.Session.State;
        var parent = Assert.Single(grouped.Selection);
        Assert.True(grouped.Rois[parent].IsGroup);
        Assert.Equal(before.Rois.Count + 1, grouped.Rois.Count);
        foreach (var roi in before.Rois.Values)
        {
            Assert.Equal(roi with
            {
                ParentId = parent
            }, grouped.Rois[roi.Id]);
        }
        Assert.Equal(view, grouped.Viewport);
        Assert.Equal(grouped.Rois.Count, model.Rows.Count);
        Assert.InRange(model.VisibleRows.Count, 1, 512);
        Assert.True(model.CanEdit);
        await model.Undo.ExecuteAsync();
        AssertDocument(before, model.Session.State);
        await model.Redo.ExecuteAsync();
        AssertDocument(grouped, model.Session.State);

        model.Ungroup.Execute(null);
        await model.EditCompletion;
        AssertDocument(before, model.Session.State);
        Assert.Equal(view, model.Session.State.Viewport);
        Assert.Equal(before.Rois.Count, model.Rows.Count);
        Assert.True(model.CanEdit);
        await model.Undo.ExecuteAsync();
        AssertDocument(grouped, model.Session.State);
        Assert.True(navigations >= 2);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EscapeCancelsGroupingCommandPreservesRedoAndAllowsTheNextEdit(bool ungroup) => OnStaAsync(async () =>
    {
        using var model = Create();
        if (ungroup)
        {
            await model.Group.ExecuteAsync();
        }
        var first = model.Session.State.Selection.First();
        model.Session.Rename(first, "redo must survive cancellation");
        model.Session.Undo();
        var before = model.Session.State;
        var bytes = model.Session.HistoryBytes;
        var command = ungroup ? model.Ungroup : model.Group;
        var cancelRequested = false;
        PropertyChangedEventHandler cancel = (_, args) =>
        {
            if (cancelRequested || args.PropertyName != nameof(model.Status) || !model.Status.Contains("준비 중"))
            {
                return;
            }
            cancelRequested = true;
            Assert.False(model.CanEdit);
            Assert.True(model.CanNavigate);
            Assert.False(command.CanExecute(null));
            Assert.True(model.HandleKey(Key.Escape, false, false));
        };
        model.PropertyChanged += cancel;
        command.Execute(null);
        await model.EditCompletion;
        // EditCompletion tracks the edit; async-void ICommand cleanup resumes separately.
        await Dispatcher.Yield(DispatcherPriority.Background);
        model.PropertyChanged -= cancel;

        Assert.True(cancelRequested);
        Assert.Contains("취소", model.Status);
        Assert.Same(before.Rois, model.Session.State.Rois);
        Assert.Same(before.Selection, model.Session.State.Selection);
        Assert.Equal(bytes, model.Session.HistoryBytes);
        Assert.True(model.Session.CanRedo);
        Assert.False(model.Session.IsPreviewing);
        Assert.True(model.CanEdit);
        Assert.True(command.CanExecute(null));
        await command.ExecuteAsync();
        Assert.True(model.CanEdit);
        Assert.False(model.Session.CanRedo);
        Assert.Equal(before.Rois.Count + (ungroup ? -1 : 1), model.Session.State.Rois.Count);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnrequestedPreparationErrorsPropagateAndReleaseEditing(bool cancellationException) => OnStaAsync(async () =>
    {
        using var model = Create();
        var before = model.Session.State;
        var failure = cancellationException ? new OperationCanceledException("unexpected cancellation")
            : (Exception)new InvalidOperationException("preparation notification failed");
        PropertyChangedEventHandler fail = (_, args) =>
        {
            if (args.PropertyName == nameof(model.Status) && model.Status.Contains("준비 중"))
            {
                throw failure;
            }
        };
        model.PropertyChanged += fail;
        var caught = await Assert.ThrowsAnyAsync<Exception>(() => model.Group.ExecuteAsync());
        model.PropertyChanged -= fail;
        Assert.Same(failure, caught);
        Assert.Same(before.Rois, model.Session.State.Rois);
        Assert.True(model.CanEdit);
        Assert.True(model.Group.CanExecute(null));
        await model.Group.ExecuteAsync();
        Assert.Equal(before.Rois.Count + 1, model.Session.State.Rois.Count);
    });

    private static MainWindowViewModel Create()
    {
        var surface = new Surface();
        var model = new MainWindowViewModel(surface, new(surface.ViewWidth, surface.ViewHeight, surface.Dpi), new UnusedLoader(), new LivePreview(surface, _ => throw new NotSupportedException()), new UnusedFilter(), EditorTestServices.Inspections());
        model.Session.InitializeImage(new("test", "test", 10000, 10000), ViewportTransform.Identity);
        model.Session.AddRange(Enumerable.Range(0, 4100).Select(index => new RoiGeometry(
            new(100 + (index % 80 * 40), 100 + (index / 80 * 40)), 20, 20, 0)));
        model.Session.Select(model.Session.State.Rois.Keys);
        return model;
    }

    private static void AssertDocument(DocumentState expected, DocumentState actual)
    {
        Assert.Equal(expected.Rois.OrderBy(pair => pair.Key), actual.Rois.OrderBy(pair => pair.Key));
        Assert.True(expected.Selection.SetEquals(actual.Selection));
        Assert.Equal(expected.Image, actual.Image);
    }

    private static void OnStaAsync(Func<Task> action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            try
            {
                var task = action();
                _ = task.ContinueWith(_ => dispatcher.BeginInvokeShutdown(DispatcherPriority.Background), TaskScheduler.Default);
                Dispatcher.Run();
                task.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                if (!dispatcher.HasShutdownStarted)
                {
                    dispatcher.InvokeShutdown();
                }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private sealed class Surface : IViewportSurface, IFramePresenter
    {
        public double ViewWidth => 1000;
        public double ViewHeight => 700;
        public double Dpi => 1;
        public event Action? LayoutChanged { add { } remove { } }
        public long SetSource(ITileSource source) => throw new NotSupportedException();
        public void Submit(RenderScene scene)
        {
        }
        public Task RestartAsync() => Task.CompletedTask;
        public void PublishFrame(VideoFrame frame) => throw new NotSupportedException();
        public void ClearFrames()
        {
        }
    }

    private sealed class UnusedLoader : IImageLoader
    {
        public Task<ITileSource> LoadAsync(ImageReference reference, IProgress<string> progress, CancellationToken token) => throw new NotSupportedException();
    }

    private sealed class UnusedFilter : IRoiFilterPreview
    {
        public Task<FilterPreviewResult> ProcessAsync(FilterRequest request, CancellationToken token) => throw new NotSupportedException();
    }
}
