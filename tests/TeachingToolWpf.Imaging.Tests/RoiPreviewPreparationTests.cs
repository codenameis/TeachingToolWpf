using System.Windows.Input;
using System.Windows.Threading;
using TeachingToolWpf.Presentation;
using VisionCanvas.Core.Editing;
using VisionCanvas.Imaging.Filters;
using VisionCanvas.Presentation;
using VisionCanvas.Rendering;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Imaging.Tests;

public class RoiPreviewPreparationTests
{
    [Fact]
    public void ColdRequestsUseOneWorkerAndApplyOnlyLatestPointerOnOwnerThread() => OnStaAsync(async () =>
    {
        var session = Create();
        var before = session.State;
        var owner = Environment.CurrentManagedThreadId;
        var gate = new Gate();
        var errors = new List<Exception>();
        var notices = 0;
        var appliedThread = 0;
        session.Changed += (_, _) =>
        {
            if (session.TransformPreview is not null)
            {
                appliedThread = Environment.CurrentManagedThreadId;
            }
        };
        using var coordinator = new RoiPreviewPreparation(session, errors.Add, () => notices++, gate.Prepare);
        session.BeginPreview();
        coordinator.SubmitTranslation(new(1, 2));
        await gate.Started.Task;
        for (var i = 2;
i <= 100;
i++)
        {
            coordinator.SubmitTranslation(new(i, i + 1));
        }

        Assert.Null(session.TransformPreview);
        Assert.Same(before.Rois, session.State.Rois);
        var flushed = coordinator.FlushAsync(default);
        gate.Release.SetResult();
        await flushed;
        Assert.Equal(1, gate.Calls);
        Assert.Equal(1, notices);
        Assert.NotEqual(owner, gate.WorkerThread);
        Assert.Equal(owner, appliedThread);
        Assert.Empty(errors);
        Assert.False(coordinator.HasPending);
        Assert.Equal(new PointD(100, 101), session.TransformPreview!.Transform.Translation);
        Assert.Same(before.Rois, session.State.Rois);
    });

    [Fact]
    public void RotationUsesLatestAngleAndReadyRequestsStaySynchronous() => OnStaAsync(async () =>
    {
        var session = Create();
        var gate = new Gate();
        using var coordinator = new RoiPreviewPreparation(session, ex => throw new Xunit.Sdk.XunitException(ex.Message), prepare: gate.Prepare);
        session.BeginPreview();
        var pivot = session.State.SelectedSpatial.Bounds.Center;
        coordinator.SubmitRotation(pivot, 10);
        await gate.Started.Task;
        coordinator.SubmitRotation(pivot, 45);
        var flushed = coordinator.FlushAsync(default);
        gate.Release.SetResult();
        await flushed;
        Assert.Equal(45, session.TransformPreview!.Transform.AngleDegrees);
        coordinator.SubmitRotation(pivot, 90);
        Assert.Equal(90, session.TransformPreview!.Transform.AngleDegrees);
        Assert.Equal(1, gate.Calls);
        session.CancelPreview();
        session.BeginPreview();
        Assert.True(session.State.IsTransformSelectionPrepared);
        coordinator.SubmitTranslation(new(3, 4));
        Assert.False(coordinator.HasPending);
        Assert.Equal(new PointD(3, 4), session.TransformPreview!.Transform.Translation);
        Assert.Equal(1, gate.Calls);
    });

    [Fact]
    public void ZeroMovementStartsNoPreparationAndReturningToOriginKeepsNoEdit() => OnStaAsync(async () =>
    {
        var session = Create();
        var before = session.State;
        var bytes = session.HistoryBytes;
        var gate = new Gate();
        using var coordinator = new RoiPreviewPreparation(session, _ => Assert.Fail("Unexpected preparation error"), prepare: gate.Prepare);
        session.BeginPreview();
        coordinator.SubmitTranslation(default);
        coordinator.SubmitRotation(default, 0);
        Assert.False(coordinator.HasPending);
        Assert.Equal(0, gate.Calls);
        coordinator.SubmitTranslation(new(10, 0));
        await gate.Started.Task;
        coordinator.SubmitTranslation(default);
        var flushed = coordinator.FlushAsync(default);
        gate.Release.SetResult();
        await flushed;
        session.CommitPreview("이동");
        Assert.Same(before.Rois, session.State.Rois);
        Assert.Equal(bytes, session.HistoryBytes);
    });

    [Fact]
    public void CancelAndRestartWithSameDocumentRejectsOldEpoch() => OnStaAsync(async () =>
    {
        var session = Create();
        var before = session.State;
        var gate = new Gate();
        var errors = new List<Exception>();
        using var coordinator = new RoiPreviewPreparation(session, errors.Add, prepare: gate.Prepare);
        session.BeginPreview();
        coordinator.SubmitTranslation(new(25, 0));
        await gate.Started.Task;
        var flushed = coordinator.FlushAsync(default);
        session.CancelPreview();
        session.BeginPreview(); // Same ROI/selection references: epoch is the necessary guard.
        Assert.Same(before.Rois, session.State.Rois);
        Assert.Same(before.Selection, session.State.Selection);
        gate.Release.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => flushed);
        Assert.Null(session.TransformPreview);
        Assert.Empty(errors);
    });

    [Fact]
    public void CancellationNeverPublishesLatePreparation() => OnStaAsync(async () =>
    {
        var session = Create();
        var gate = new Gate();
        var errors = new List<Exception>();
        using var coordinator = new RoiPreviewPreparation(session, errors.Add, prepare: gate.Prepare);
        session.BeginPreview();
        coordinator.SubmitTranslation(new(25, 0));
        await gate.Started.Task;
        var flushed = coordinator.FlushAsync(default);
        coordinator.Cancel();
        session.CancelPreview();
        gate.Release.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => flushed);
        Assert.Null(session.TransformPreview);
        Assert.False(coordinator.HasPending);
        Assert.Empty(errors);
    });

    [Fact]
    public void PreparationFailureIsReportedAndFlushCannotCommitIt() => OnStaAsync(async () =>
    {
        var session = Create();
        var errors = new List<Exception>();
        var failure = new InvalidOperationException("preparation failed");
        using var coordinator = new RoiPreviewPreparation(session, errors.Add, prepare: (_, _) => throw failure);
        session.BeginPreview();
        coordinator.SubmitTranslation(new(25, 0));
        var caught = await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.FlushAsync(default));
        Assert.Same(failure, caught);
        Assert.Same(failure, Assert.Single(errors));
        Assert.Null(session.TransformPreview);
    });

    [Fact]
    public void CancelRestartSerializesWarmEvenWhenOldPreparationIgnoresCancellation() => OnStaAsync(async () =>
    {
        var session = Create();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var active = 0;
        var maximum = 0;
        var errors = new List<Exception>();
        using var coordinator = new RoiPreviewPreparation(session, errors.Add, prepare: (source, token) =>
        {
            var concurrent = Interlocked.Increment(ref active);
            lock (release)
            {
                maximum = Math.Max(maximum, concurrent);
            }

            try
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    started.SetResult();
                    // Simulate a running Lazy factory: cancellation cannot release its worker slot yet.
                    release.Task.GetAwaiter().GetResult();
                    return;
                }
                RoiPreviewPreparation.Warm(source, token);
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        });
        session.BeginPreview();
        coordinator.SubmitTranslation(new(10, 0));
        await started.Task;
        var first = coordinator.FlushAsync(default);
        coordinator.Cancel();
        session.CancelPreview();
        session.BeginPreview();
        coordinator.SubmitTranslation(new(20, 0));
        var canceledWaiter = coordinator.FlushAsync(default);
        coordinator.Cancel();
        session.CancelPreview();
        // A canceled waiter exits before the noncooperative first worker is released.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledWaiter);
        Assert.Equal(1, Volatile.Read(ref calls));
        Assert.Equal(1, Volatile.Read(ref active));
        session.BeginPreview();
        coordinator.SubmitTranslation(new(30, 0));
        coordinator.SubmitTranslation(new(40, 0));
        var latest = coordinator.FlushAsync(default);
        release.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await latest;
        Assert.Equal(2, calls);
        Assert.Equal(1, maximum);
        Assert.Equal(0, active);
        Assert.Empty(errors);
        Assert.Equal(new PointD(40, 0), session.TransformPreview!.Transform.Translation);
    });

    [Fact]
    public void DisposeRetiresGateAfterRunningWorkerAndCanceledWaiterExit() => OnStaAsync(async () =>
    {
        var session = Create();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var errors = new List<Exception>();
        var coordinator = new RoiPreviewPreparation(session, errors.Add, prepare: (_, _) =>
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult();
            release.Task.GetAwaiter().GetResult();
        });
        session.BeginPreview();
        coordinator.SubmitTranslation(new(10, 0));
        await started.Task;
        var first = coordinator.FlushAsync(default);
        coordinator.Cancel();
        session.CancelPreview();
        session.BeginPreview();
        coordinator.SubmitTranslation(new(20, 0));
        var waiting = coordinator.FlushAsync(default);
        coordinator.Dispose();
        coordinator.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        release.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Equal(1, calls);
        Assert.Empty(errors);
        Assert.Null(session.TransformPreview);
        Assert.Throws<ObjectDisposedException>(() => coordinator.SubmitTranslation(new(30, 0)));
    });

    [Fact]
    public void MouseUpBeforePreparationOwnsOnePendingEditAndPreservesPan() => OnStaAsync(async () =>
    {
        var gate = new Gate();
        var surface = new Surface();
        using var model = new EditorViewModel(surface, new UnusedLoader(), new LivePreview(surface, _ => throw new NotSupportedException()), new UnusedFilter(), gate.Prepare);
        Populate(model.Session);
        var id = model.Session.State.Selection.Single();
        var before = model.Session.State;
        var start = before.Rois[id].Geometry.Center;
        model.Input.Down(start);
        model.Input.Move(start + new PointD(6, 0));
        await gate.Started.Task;
        model.Input.Up(start + new PointD(15, 0));
        Assert.False(model.CanEdit);
        Assert.True(model.CanNavigate);
        Assert.False(model.EditCompletion.IsCompleted);
        var view = before.Viewport.Pan(new(25, 12));
        model.Session.SetViewport(view);
        gate.Release.SetResult();
        await model.EditCompletion;
        Assert.True(model.CanEdit);
        Assert.False(model.Session.IsPreviewing);
        Assert.Equal(1, gate.Calls);
        Assert.Equal(start + new PointD(15, 0), model.Session.State.Rois[id].Geometry.Center);
        Assert.Equal(view, model.Session.State.Viewport);
        model.Session.Undo();
        Assert.Equal(before.Rois[id], model.Session.State.Rois[id]);
    });

    [Fact]
    public void EscapeAfterMouseUpCancelsPendingEditWithoutGeometryOrHistoryChange() => OnStaAsync(async () =>
    {
        var gate = new Gate();
        var surface = new Surface();
        using var model = new EditorViewModel(surface, new UnusedLoader(), new LivePreview(surface, _ => throw new NotSupportedException()), new UnusedFilter(), gate.Prepare);
        Populate(model.Session);
        var before = model.Session.State;
        var bytes = model.Session.HistoryBytes;
        var start = before.Rois[before.Selection.Single()].Geometry.Center;
        model.Input.Down(start);
        model.Input.Move(start + new PointD(6, 0));
        await gate.Started.Task;
        model.Input.Up(start + new PointD(15, 0));
        model.HandleKey(Key.Escape, false, false);
        gate.Release.SetResult();
        await model.EditCompletion;
        Assert.True(model.CanEdit);
        Assert.False(model.Session.IsPreviewing);
        Assert.Same(before.Rois, model.Session.State.Rois);
        Assert.Equal(bytes, model.Session.HistoryBytes);
    });

    private static EditorSession Create()
    {
        var session = new EditorSession();
        Populate(session);
        return session;
    }

    private static void Populate(EditorSession session)
    {
        session.InitializeImage(new("test", "test", 10000, 10000), new(1, default));
        session.AddRange(Enumerable.Range(0, 4100).Select(i => new RoiGeometry(new(100 + (i % 80 * 40), 100 + (i / 80 * 40)), 20, 20, 0)));
        session.Select([session.State.Rois.Values.MinBy(r => r.Order)!.Id]);
    }

    private sealed class Gate
    {
        public TaskCompletionSource Started
        {
            get;
        } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release
        {
            get;
        } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Calls;

        public int WorkerThread;

        public void Prepare(DocumentState state, CancellationToken token)
        {
            Interlocked.Increment(ref Calls);
            WorkerThread = Environment.CurrentManagedThreadId;
            Started.TrySetResult();
            Release.Task.WaitAsync(token).GetAwaiter().GetResult();
            RoiPreviewPreparation.Warm(state, token);
        }
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

        public event Action? LayoutChanged
        {
            add
            {
            }
            remove
            {
            }
        }

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
