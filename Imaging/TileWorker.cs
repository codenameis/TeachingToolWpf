using System.Collections.Concurrent;
using TeachingToolWpf.Core.Imaging;

namespace TeachingToolWpf.Imaging;

public sealed record ReadyTile(long Generation, TileKey Key, PixelBuffer Pixels);

/// <summary>One decoding owner, a coalesced request and at most 32 prepared tiles.</summary>
public sealed class TileWorker : IDisposable
{
    private readonly object gate = new();
    private readonly AutoResetEvent wake = new(false);
    private readonly SemaphoreSlim slots = new(32, 32);
    private readonly CancellationTokenSource shutdown = new();
    private readonly ConcurrentQueue<ReadyTile> ready = new();
    private readonly Thread thread;
    private ITileSource? pendingSource;
    private long generation;
    private Request? pendingRequest;
    private sealed record Request(long Generation, IReadOnlyList<TileKey> Keys);
    public event Action? TileReady;
    public event Action<string>? Failed;
    public long Generation => Interlocked.Read(ref generation);
    public bool HasReady => !ready.IsEmpty;
    public TileWorker()
    {
        thread = new Thread(Run) { IsBackground = true, Name = "Image tile decoder" };
        thread.Start();
    }
    public long SetSource(ITileSource source)
    {
        lock (gate)
        {
            pendingSource?.Dispose(); pendingSource = source; pendingRequest = null;
            Interlocked.Increment(ref generation);
        }
        wake.Set(); return Generation;
    }
    public void RequestTiles(long expectedGeneration, IReadOnlyList<TileKey> keys)
    {
        if (keys.Count == 0) return;
        lock (gate) pendingRequest = new(expectedGeneration, keys);
        wake.Set();
    }
    public bool TryTake(out ReadyTile? tile)
    {
        if (!ready.TryDequeue(out tile)) return false;
        slots.Release(); return true;
    }
    private void Run()
    {
        ITileSource? source = null;
        long sourceGeneration = 0;
        long failedGeneration = -1;
        try
        {
            while (!shutdown.IsCancellationRequested)
            {
                wake.WaitOne();
                Request? request;
                lock (gate)
                {
                    if (pendingSource is { } next)
                    { source?.Dispose(); source = next; pendingSource = null; sourceGeneration = generation; }
                    request = pendingRequest; pendingRequest = null;
                }
                if (source is null || request is null || request.Generation != sourceGeneration || sourceGeneration == failedGeneration) continue;
                foreach (var key in request.Keys)
                {
                    if (Generation != sourceGeneration || shutdown.IsCancellationRequested) break;
                    slots.Wait(shutdown.Token);
                    PixelBuffer? pixels = null;
                    var queued = false;
                    try
                    {
                        pixels = source.Read(key, shutdown.Token);
                        if (Generation == sourceGeneration)
                        { ready.Enqueue(new(sourceGeneration, key, pixels)); pixels = null; queued = true; }
                    }
                    catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        failedGeneration = sourceGeneration;
                        Failed?.Invoke($"타일 읽기 실패: {ex.Message}. 이미지를 다시 열어 재시도하세요."); break;
                    }
                    finally
                    {
                        pixels?.Dispose();
                        // A queued buffer owns the slot until TryTake. Notification is not part of decoding.
                        if (!queued) slots.Release();
                    }
                    if (queued) TileReady?.Invoke();
                    lock (gate) if (pendingRequest is not null) break;
                }
            }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
        catch (Exception ex) { Failed?.Invoke($"타일 읽기 실패: {ex.Message}"); }
        finally { source?.Dispose(); }
    }
    public void Dispose()
    {
        shutdown.Cancel(); wake.Set(); thread.Join();
        lock (gate) { pendingSource?.Dispose(); pendingSource = null; }
        while (ready.TryDequeue(out var item)) item.Pixels.Dispose();
        shutdown.Dispose(); wake.Dispose(); slots.Dispose();
    }
}
