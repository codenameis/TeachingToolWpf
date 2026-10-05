using VisionImaging.Abstractions;

namespace VisionImaging;

/// <summary>Single-slot ownership transfer. Replaced frames are returned immediately.</summary>
public sealed class LatestFrameMailbox : IDisposable
{
    private readonly object _gate = new();
    private VideoFrame? _pending;
    private VideoFrame? _latest;
    private bool _disposed;
    private long _dropped;

    public long Dropped => Interlocked.Read(ref _dropped);

    public bool HasPending
    {
        get
        {
            lock (_gate)
            {
                return _pending is not null;
            }
        }
    }

    public event Action? Available;

    public void Publish(VideoFrame frame)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                frame.Dispose();
                return;
            }
            if (_pending is not null)
            {
                _pending.Dispose();
                Interlocked.Increment(ref _dropped);
            }
            _latest?.Dispose();
            _latest = frame.Retain();
            _pending = frame;
            Available?.Invoke();
        }
    }

    public VideoFrame? Take()
    {
        lock (_gate)
        {
            var frame = _pending;
            _pending = null;
            return frame;
        }
    }

    public VideoFrame? RetainLatest()
    {
        lock (_gate)
        {
            return _latest?.Retain();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _pending?.Dispose();
            _latest?.Dispose();
            _pending = null;
            _latest = null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            Clear();
            Available = null;
        }
    }
}
