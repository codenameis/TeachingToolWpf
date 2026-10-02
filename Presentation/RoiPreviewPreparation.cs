using System.Runtime.ExceptionServices;
using TeachingToolWpf.Core.Editing;
using TeachingToolWpf.Core.Geometry;

namespace TeachingToolWpf.Presentation;

/// <summary>Owner-thread coordinator: one cold preparation and the latest pointer request per gesture.</summary>
internal sealed class RoiPreviewPreparation(EditorSession session, Action<Exception> onError,
    Action? onPreparing = null, Action<DocumentState, CancellationToken>? prepare = null) : IDisposable
{
    private readonly SemaphoreSlim workerGate = new(1, 1);
    private Pending? pending;
    private int runningOperations;
    private bool disposed, workerGateDisposed;
    public bool HasPending => pending is not null;

    public void SubmitTranslation(PointD delta) => Submit(new(delta, 0, false));
    public void SubmitRotation(PointD pivot, double degrees) => Submit(new(pivot, degrees, true));

    private void Submit(Request request)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!session.IsPreviewing) throw new InvalidOperationException("편집 preview가 시작되지 않았습니다.");
        if (pending is { } previous && !Matches(previous)) Cancel();
        if (pending is { } existing) { existing.Latest = request; return; }
        // Down/Up without movement must not warm a large subtree or create a history entry.
        if (request.IsIdentity && session.TransformPreview is null) return;
        var source = session.State;
        if (source.Rois.Count <= 4096 || source.IsTransformSelectionPrepared || session.TransformPreview is not null)
        { Apply(request); return; }
        var operation = new Pending(source, session.PreviewEpoch, request);
        pending = operation;
        onPreparing?.Invoke();
        runningOperations++;
        operation.Completion = RunAsync(operation);
    }

    private async Task RunAsync(Pending operation)
    {
        var ownsWorker = false;
        try
        {
            // Cancellation can outlive an uninterruptible Lazy factory. Keep its worker slot until it exits.
            await workerGate.WaitAsync(operation.Cancellation.Token);
            ownsWorker = true;
            await EditPreparation.Run(() =>
            {
                (prepare ?? Warm)(operation.Source, operation.Cancellation.Token);
                operation.Cancellation.Token.ThrowIfCancellationRequested();
                return true;
            }, operation.Cancellation.Token);
            if (!Matches(operation) || operation.Cancellation.IsCancellationRequested) return;
            Apply(operation.Latest);
            operation.Applied = true;
            pending = null;
        }
        catch (OperationCanceledException) when (operation.Cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            operation.Failure = ExceptionDispatchInfo.Capture(ex);
            if (Matches(operation)) onError(ex);
        }
        finally
        {
            if (ownsWorker) workerGate.Release();
            operation.Cancellation.Dispose();
            runningOperations--;
            RetireGate();
        }
    }

    internal static void Warm(DocumentState source, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); var selection = source.TransformSelection;
        token.ThrowIfCancellationRequested(); _ = selection.Moving;
        token.ThrowIfCancellationRequested(); _ = selection.Stationary;
        token.ThrowIfCancellationRequested(); _ = selection.RotationLimits(selection.Bounds.Center);
        token.ThrowIfCancellationRequested();
    }

    // Mouse Up transfers completion ownership to the existing cancellable edit transaction.
    public async Task FlushAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (pending is not { } operation) return;
        try { await operation.Completion.WaitAsync(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (ReferenceEquals(pending, operation)) Cancel();
            throw;
        }
        token.ThrowIfCancellationRequested();
        operation.Failure?.Throw();
        if (!operation.Applied || !MatchesSource(operation)) throw new OperationCanceledException("선택 영역 준비가 취소되었습니다.");
    }

    private bool Matches(Pending operation) => ReferenceEquals(pending, operation) && MatchesSource(operation);
    private bool MatchesSource(Pending operation) => !disposed && session.IsPreviewing && session.PreviewEpoch == operation.Epoch
        && ReferenceEquals(session.State.Rois, operation.Source.Rois)
        && ReferenceEquals(session.State.Selection, operation.Source.Selection) && session.State.Image == operation.Source.Image;

    private void Apply(Request request)
    {
        if (request.Rotation) session.PreviewRotation(request.Point, request.Degrees);
        else session.PreviewTranslation(request.Point);
    }

    public void Cancel()
    {
        if (pending is not { } operation) return;
        pending = null;
        if (!operation.Completion.IsCompleted) operation.Cancellation.Cancel();
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; Cancel();
        // Owner-thread continuations retire the gate after all holders and canceled waiters have exited.
        RetireGate();
    }
    private void RetireGate()
    {
        if (!disposed || runningOperations != 0 || workerGateDisposed) return;
        workerGateDisposed = true; workerGate.Dispose();
    }

    private readonly record struct Request(PointD Point, double Degrees, bool Rotation)
    { public bool IsIdentity => Rotation ? Degrees == 0 : Point == default; }
    private sealed class Pending(DocumentState source, long epoch, Request latest)
    {
        public DocumentState Source { get; } = source;
        public long Epoch { get; } = epoch;
        public Request Latest { get; set; } = latest;
        public CancellationTokenSource Cancellation { get; } = new();
        public Task Completion { get; set; } = Task.CompletedTask;
        public ExceptionDispatchInfo? Failure { get; set; }
        public bool Applied { get; set; }
    }
}
