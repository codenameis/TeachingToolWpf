namespace TeachingToolWpf.Core.Imaging;

public sealed class VideoFrame : IDisposable
{
    private sealed class SharedPixels(PixelBuffer pixels)
    {
        private int _references = 1;

        public PixelBuffer Pixels
        {
            get;
        } = pixels;

        public void Retain()
        {
            int count;
            do
            {
                count = Volatile.Read(ref _references);
                if (count == 0)
                {
                    throw new ObjectDisposedException(nameof(VideoFrame));
                }
            }
            while (Interlocked.CompareExchange(ref _references, count + 1, count) != count);
        }

        public void Release()
        {
            if (Interlocked.Decrement(ref _references) == 0)
            {
                Pixels.Dispose();
            }
        }
    }
    private readonly SharedPixels _shared;
    private int _disposed;

    public long Sequence
    {
        get;
    }

    public long Timestamp
    {
        get;
    }

    public PixelBuffer Pixels => Volatile.Read(ref _disposed) == 0 ? _shared.Pixels : throw new ObjectDisposedException(nameof(VideoFrame));

    public VideoFrame(long sequence, long timestamp, PixelBuffer pixels)
    {
        Sequence = sequence;
        Timestamp = timestamp;
        _shared = new(pixels);
    }

    private VideoFrame(long sequence, long timestamp, SharedPixels pixels)
    {
        Sequence = sequence;
        Timestamp = timestamp;
        _shared = pixels;
    }

    public VideoFrame Retain()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(VideoFrame));
        }

        _shared.Retain();
        return new(Sequence, Timestamp, _shared);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _shared.Release();
        }
    }
}
public interface IFrameSource
{
    ImageInfo Info
    {
        get;
    }
    IAsyncEnumerable<VideoFrame> Frames(CancellationToken cancellationToken);
}

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
