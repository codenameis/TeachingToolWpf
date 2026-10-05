namespace VisionImaging.Abstractions;

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
