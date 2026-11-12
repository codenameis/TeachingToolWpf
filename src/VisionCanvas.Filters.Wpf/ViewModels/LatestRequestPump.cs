namespace VisionCanvas.Filters.Wpf.ViewModels;

/// <summary>
/// 단일 비동기 작업자와 교체 가능한 최신 대기 요청 1개를 유지하는 디바운스 실행 펌프.
/// </summary>
public sealed class LatestRequestPump<TRequest>(TimeSpan debounceDelay) : IDisposable
    where TRequest : class
{
    private TRequest? _pending;
    private TRequest? _lastRequested;
    private CancellationTokenSource? _cancellation;
    private long _revision;
    private Task? _pumpTask;
    private bool _disposed;

    public TRequest? LastRequested => _lastRequested;

    public bool HasPending => _pending is not null;

    public bool IsBusy { get; private set; }

    public Task WhenIdle => _pumpTask ?? Task.CompletedTask;

    public void Enqueue(
        TRequest request,
        bool invalidateInFlight,
        Func<TRequest, long, CancellationToken, Task> executeAsync,
        Action<Exception> onError,
        Action<bool> onBusyChanged)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(executeAsync);
        ArgumentNullException.ThrowIfNull(onError);
        ArgumentNullException.ThrowIfNull(onBusyChanged);

        if (_disposed)
        {
            return;
        }

        _pending = _lastRequested = request;
        if (invalidateInFlight)
        {
            _revision++;
            _cancellation?.Cancel();
        }

        if (_pumpTask is null || _pumpTask.IsCompleted)
        {
            _pumpTask = RunLoopAsync(executeAsync, onError, onBusyChanged);
        }
    }

    public bool IsCurrentRevision(long capturedRevision) => !_disposed && capturedRevision == _revision;

    public void CancelPending()
    {
        _revision++;
        _pending = _lastRequested = null;
        _cancellation?.Cancel();
    }

    public void Dispose()
    {
        _disposed = true;
        CancelPending();
    }

    private async Task RunLoopAsync(
        Func<TRequest, long, CancellationToken, Task> executeAsync,
        Action<Exception> onError,
        Action<bool> onBusyChanged)
    {
        IsBusy = true;
        onBusyChanged(true);
        try
        {
            while (_pending is not null && !_disposed)
            {
                var capturedRevision = _revision;
                using var current = new CancellationTokenSource();
                _cancellation = current;
                try
                {
                    await Task.Delay(debounceDelay, current.Token);
                    if (_pending is not { } request)
                    {
                        continue;
                    }

                    _pending = null;
                    await executeAsync(request, capturedRevision, current.Token);
                }
                catch (OperationCanceledException) when (current.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {
                    if (capturedRevision == _revision && !_disposed)
                    {
                        onError(ex);
                    }
                }
                finally
                {
                    if (ReferenceEquals(_cancellation, current))
                    {
                        _cancellation = null;
                    }
                }
            }
        }
        finally
        {
            IsBusy = false;
            onBusyChanged(false);
        }
    }
}
