using System.Runtime.ExceptionServices;
using VisionCanvas.Core.Editing;
using VisionGeometry;

namespace TeachingToolWpf.Presentation;

/// <summary>UI 스레드가 준비 작업 하나와 해당 드래그의 마지막 포인터 요청을 관리한다.</summary>
internal sealed class RoiPreviewPreparation(EditorSession session, Action<Exception> onError,
    Action? onPreparing = null, Action<DocumentState, CancellationToken>? prepare = null) : IDisposable
{
    private readonly SemaphoreSlim _workerGate = new(1, 1);
    private Pending? _pending;
    private int _runningOperations;
    private bool _disposed;
    private bool _workerGateDisposed;

    public bool HasPending => _pending is not null;

    public void SubmitTranslation(PointD delta) => Submit(new(delta, 0, false));

    public void SubmitRotation(PointD pivot, double degrees) => Submit(new(pivot, degrees, true));

    private void Submit(Request request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!session.IsPreviewing)
        {
            throw new InvalidOperationException("편집 preview가 시작되지 않았습니다.");
        }

        if (_pending is { } previous && !IsCurrentOperation(previous))
        {
            Cancel();
        }

        if (_pending is { } existing)
        {
            existing.Latest = request;
            return;
        }

        // 클릭만 하고 놓으면 큰 하위 트리를 준비하거나 이력을 만들 필요가 없다.
        if (request.IsIdentity && session.TransformPreview is null)
        {
            return;
        }

        var source = session.State;
        if (CanApplyImmediately(source))
        {
            Apply(request);
            return;
        }

        var operation = new Pending(source, session.PreviewEpoch, request);
        _pending = operation;
        onPreparing?.Invoke();
        _runningOperations++;
        operation.Completion = RunAsync(operation);
    }

    private bool CanApplyImmediately(DocumentState source)
        => source.Rois.Count <= 4096 || source.IsTransformSelectionPrepared || session.TransformPreview is not null;

    private async Task RunAsync(Pending operation)
    {
        var ownsWorker = false;
        try
        {
            // Lazy 계산은 취소 직후에도 실행 중일 수 있으므로, 실제 종료까지 worker 자리를 유지한다.
            await _workerGate.WaitAsync(operation.Cancellation.Token);
            ownsWorker = true;
            await EditPreparation.Run(() =>
            {
                (prepare ?? Warm)(operation.Source, operation.Cancellation.Token);
                operation.Cancellation.Token.ThrowIfCancellationRequested();
                return true;
            }, operation.Cancellation.Token);
            if (!IsCurrentOperation(operation) || operation.Cancellation.IsCancellationRequested)
            {
                return;
            }

            // 기다리는 동안 누적된 요청 중 마지막 값만 UI 스레드에서 적용한다.
            Apply(operation.Latest);
            operation.Applied = true;
            _pending = null;
        }
        catch (OperationCanceledException) when (operation.Cancellation.IsCancellationRequested)
        {
            // 취소된 작업은 문서에 게시하지 않는다. Mouse Up의 FlushAsync가 취소 결과를 확인한다.
        }
        catch (Exception ex)
        {
            operation.Failure = ExceptionDispatchInfo.Capture(ex);
            if (IsCurrentOperation(operation))
            {
                onError(ex);
            }
        }
        finally
        {
            if (ownsWorker)
            {
                _workerGate.Release();
            }

            operation.Cancellation.Dispose();
            _runningOperations--;
            DisposeWorkerGateWhenIdle();
        }
    }

    internal static void Warm(DocumentState source, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var selection = source.TransformSelection;
        token.ThrowIfCancellationRequested();
        _ = selection.Moving;
        token.ThrowIfCancellationRequested();
        _ = selection.Stationary;
        token.ThrowIfCancellationRequested();
        _ = selection.RotationLimits(selection.Bounds.Center);
        token.ThrowIfCancellationRequested();
    }

    // Mouse Up 이후에는 기존 편집 트랜잭션이 준비 완료 대기와 취소를 이어받는다.
    public async Task FlushAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_pending is not { } operation)
        {
            return;
        }

        try
        {
            await operation.Completion.WaitAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (ReferenceEquals(_pending, operation))
            {
                Cancel();
            }

            throw;
        }
        token.ThrowIfCancellationRequested();
        operation.Failure?.Throw();
        if (!operation.Applied || !IsCurrentGesture(operation))
        {
            throw new OperationCanceledException("선택 영역 준비가 취소되었습니다.");
        }
    }

    private bool IsCurrentOperation(Pending operation)
        => ReferenceEquals(_pending, operation) && IsCurrentGesture(operation);

    private bool IsCurrentGesture(Pending operation)
        => !_disposed && session.IsPreviewing && session.PreviewEpoch == operation.Epoch
            && ReferenceEquals(session.State.Rois, operation.Source.Rois)
            && ReferenceEquals(session.State.Selection, operation.Source.Selection)
            && session.State.Image == operation.Source.Image;

    private void Apply(Request request)
    {
        if (request.IsRotation)
        {
            session.PreviewRotation(request.Point, request.Degrees);
        }
        else
        {
            session.PreviewTranslation(request.Point);
        }
    }

    public void Cancel()
    {
        if (_pending is not { } operation)
        {
            return;
        }

        _pending = null;
        if (!operation.Completion.IsCompleted)
        {
            operation.Cancellation.Cancel();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Cancel();
        // 실행 중인 작업과 취소된 대기 작업까지 모두 끝난 뒤 gate를 해제한다.
        DisposeWorkerGateWhenIdle();
    }

    private void DisposeWorkerGateWhenIdle()
    {
        if (!_disposed || _runningOperations != 0 || _workerGateDisposed)
        {
            return;
        }

        _workerGateDisposed = true;
        _workerGate.Dispose();
    }

    private readonly record struct Request(PointD Point, double Degrees, bool IsRotation)
    {
        public bool IsIdentity => IsRotation ? Degrees == 0 : Point == default;
    }
    private sealed class Pending(DocumentState source, long epoch, Request latest)
    {
        public DocumentState Source
        {
            get;
        } = source;

        public long Epoch
        {
            get;
        } = epoch;

        public Request Latest
        {
            get;
            set;
        } = latest;

        public CancellationTokenSource Cancellation
        {
            get;
        } = new();

        public Task Completion
        {
            get;
            set;
        } = Task.CompletedTask;

        public ExceptionDispatchInfo? Failure
        {
            get;
            set;
        }

        public bool Applied
        {
            get;
            set;
        }
    }
}
