using System.Collections.Concurrent;
using TeachingToolWpf.Core.Editing;
using TeachingToolWpf.Core.Imaging;
using TeachingToolWpf.Imaging;

internal sealed record TileReadCounts(long TotalTileReads, IReadOnlyDictionary<int, long> TileReadsByLevel);

/// <summary>서비스가 연 독립 reader의 원본 요청만 계측하며 픽셀 값이나 소스 소유권은 바꾸지 않는다.</summary>
internal sealed class CountingImageLoader(IImageLoader inner) : IImageLoader
{
    private readonly ConcurrentDictionary<int, long> _readsByLevel = new();
    private long _reads;

    public async Task<ITileSource> LoadAsync(ImageReference reference, IProgress<string> progress, CancellationToken cancellationToken)
        => new Source(this, await inner.LoadAsync(reference, progress, cancellationToken));

    public void Reset()
    {
        Interlocked.Exchange(ref _reads, 0);
        _readsByLevel.Clear();
    }

    public TileReadCounts Snapshot() => new(Interlocked.Read(ref _reads),
        _readsByLevel.OrderBy(pair => pair.Key).ToDictionary(pair => pair.Key, pair => pair.Value));

    private sealed class Source(CountingImageLoader owner, ITileSource inner) : ITileSource
    {
        public ImageInfo Info => inner.Info;

        public PixelBuffer Read(TileKey key, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref owner._reads);
            owner._readsByLevel.AddOrUpdate(key.Level, 1, (_, count) => count + 1);
            return inner.Read(key, cancellationToken);
        }

        public void Dispose() => inner.Dispose();
    }
}
