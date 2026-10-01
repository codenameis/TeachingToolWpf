namespace TeachingToolWpf.Core.Imaging;

public sealed class VideoFrame : IDisposable
{
    private sealed class SharedPixels(PixelBuffer pixels)
    {
        private int references = 1;
        public PixelBuffer Pixels { get; } = pixels;
        public void Retain()
        {
            int count;
            do { count = Volatile.Read(ref references); if (count == 0) throw new ObjectDisposedException(nameof(VideoFrame)); }
            while (Interlocked.CompareExchange(ref references, count + 1, count) != count);
        }
        public void Release() { if (Interlocked.Decrement(ref references) == 0) Pixels.Dispose(); }
    }
    private readonly SharedPixels shared;
    private int disposed;
    public long Sequence { get; }
    public long Timestamp { get; }
    public PixelBuffer Pixels => Volatile.Read(ref disposed) == 0 ? shared.Pixels : throw new ObjectDisposedException(nameof(VideoFrame));
    public VideoFrame(long sequence, long timestamp, PixelBuffer pixels) { Sequence = sequence; Timestamp = timestamp; shared = new(pixels); }
    private VideoFrame(long sequence, long timestamp, SharedPixels pixels) { Sequence = sequence; Timestamp = timestamp; shared = pixels; }
    public VideoFrame Retain()
    {
        if (Volatile.Read(ref disposed) != 0) throw new ObjectDisposedException(nameof(VideoFrame));
        shared.Retain(); return new(Sequence, Timestamp, shared);
    }
    public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) shared.Release(); }
}
public interface IFrameSource
{
    ImageInfo Info { get; }
    IAsyncEnumerable<VideoFrame> Frames(CancellationToken cancellationToken);
}

/// <summary>Single-slot ownership transfer. Replaced frames are returned immediately.</summary>
public sealed class LatestFrameMailbox : IDisposable
{
    private readonly object gate = new();
    private VideoFrame? pending;
    private VideoFrame? latest;
    private bool disposed;
    private long dropped;
    public long Dropped => Interlocked.Read(ref dropped);
    public bool HasPending { get { lock (gate) return pending is not null; } }
    public event Action? Available;
    public void Publish(VideoFrame frame)
    {
        lock (gate)
        {
            if (disposed) { frame.Dispose(); return; }
            if (pending is not null) { pending.Dispose(); Interlocked.Increment(ref dropped); }
            latest?.Dispose(); latest = frame.Retain();
            pending = frame;
            Available?.Invoke();
        }
    }
    public VideoFrame? Take() { lock (gate) { var frame = pending; pending = null; return frame; } }
    public VideoFrame? RetainLatest() { lock (gate) return latest?.Retain(); }
    public void Clear() { lock (gate) { pending?.Dispose(); latest?.Dispose(); pending = null; latest = null; } }
    public void Dispose() { lock (gate) { disposed = true; Clear(); Available = null; } }
}
