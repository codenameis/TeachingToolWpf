using VisionImaging.Abstractions;

namespace VisionCanvas.Sample;

/// <summary>타일 데이터의 복사 없이 샘플에서 읽기 크기와 해제 여부만 관측한다.</summary>
internal sealed class ObservedTileSource(ITileSource source) : ITileSource
{
    private int _readCount;
    private int _maximumBufferBytes;
    private int _disposed;

    public ImageInfo Info => source.Info;
    public int ReadCount => Volatile.Read(ref _readCount);
    public int MaximumBufferBytes => Volatile.Read(ref _maximumBufferBytes);
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public PixelBuffer Read(TileKey key, CancellationToken cancellationToken)
    {
        var pixels = source.Read(key, cancellationToken);
        Interlocked.Increment(ref _readCount);
        // ITileSource는 한 worker에서 직렬 읽기하므로 writer는 하나다.
        Volatile.Write(ref _maximumBufferBytes, Math.Max(_maximumBufferBytes, pixels.Bytes.Length));
        return pixels;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            source.Dispose();
        }
    }
}
