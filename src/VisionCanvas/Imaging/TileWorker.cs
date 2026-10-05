using System.Collections.Concurrent;
using VisionImaging.Abstractions;

namespace VisionCanvas.Imaging;

public sealed record ReadyTile(long Generation, TileKey Key, PixelBuffer Pixels);

/// <summary>One decoding owner, a coalesced request and at most 32 prepared tiles.</summary>
public sealed class TileWorker : IDisposable
{
    private readonly object _gate = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly SemaphoreSlim _slots = new(32, 32);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentQueue<ReadyTile> _ready = new();
    private readonly Thread _thread;
    private ITileSource? _pendingSource;
    private long _generation;
    private Request? _pendingRequest;
    private sealed record Request(long Generation, IReadOnlyList<TileKey> Keys);
    public event Action? TileReady;
    public event Action<string>? Failed;

    public long Generation => Interlocked.Read(ref _generation);

    public bool HasReady => !_ready.IsEmpty;

    public TileWorker()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Image tile decoder" };
        _thread.Start();
    }

    public long SetSource(ITileSource source)
    {
        lock (_gate)
        {
            _pendingSource?.Dispose();
            _pendingSource = source;
            _pendingRequest = null;
            Interlocked.Increment(ref _generation);
        }
        _wake.Set();
        return Generation;
    }

    public void RequestTiles(long expectedGeneration, IReadOnlyList<TileKey> keys)
    {
        if (keys.Count == 0)
        {
            return;
        }

        lock (_gate)
        {
            _pendingRequest = new(expectedGeneration, keys);
        }

        _wake.Set();
    }

    public bool TryTake(out ReadyTile? tile)
    {
        if (!_ready.TryDequeue(out tile))
        {
            return false;
        }

        _slots.Release();
        return true;
    }

    private void Run()
    {
        ITileSource? source = null;
        long sourceGeneration = 0;
        long failedGeneration = -1;
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                _wake.WaitOne();
                Request? request;
                lock (_gate)
                {
                    if (_pendingSource is { } next)
                    {
                        source?.Dispose();
                        source = next;
                        _pendingSource = null;
                        sourceGeneration = _generation;
                    }
                    request = _pendingRequest;
                    _pendingRequest = null;
                }
                if (source is null || request is null || request.Generation != sourceGeneration || sourceGeneration == failedGeneration)
                {
                    continue;
                }

                foreach (var key in request.Keys)
                {
                    if (Generation != sourceGeneration || _shutdown.IsCancellationRequested)
                    {
                        break;
                    }

                    _slots.Wait(_shutdown.Token);
                    PixelBuffer? pixels = null;
                    var queued = false;
                    try
                    {
                        pixels = source.Read(key, _shutdown.Token);
                        if (Generation == sourceGeneration)
                        {
                            _ready.Enqueue(new(sourceGeneration, key, pixels));
                            pixels = null;
                            queued = true;
                        }
                    }
                    catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        failedGeneration = sourceGeneration;
                        Failed?.Invoke($"타일 읽기 실패: {ex.Message}. 이미지를 다시 열어 재시도하세요.");
                        break;
                    }
                    finally
                    {
                        pixels?.Dispose();
                        // A queued buffer owns the slot until TryTake. Notification is not part of decoding.
                        if (!queued)
                        {
                            _slots.Release();
                        }
                    }
                    if (queued)
                    {
                        TileReady?.Invoke();
                    }

                    lock (_gate)
                    {
                        if (_pendingRequest is not null)
                        {
                            break;
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Failed?.Invoke($"타일 읽기 실패: {ex.Message}");
        }
        finally
        {
            source?.Dispose();
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _wake.Set();
        _thread.Join();
        lock (_gate)
        {
            _pendingSource?.Dispose();
            _pendingSource = null;
        }
        while (_ready.TryDequeue(out var item))
        {
            item.Pixels.Dispose();
        }

        _shutdown.Dispose();
        _wake.Dispose();
        _slots.Dispose();
    }
}
