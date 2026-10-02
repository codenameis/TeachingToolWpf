using System.Collections.Concurrent;
using TeachingToolWpf.Core.Imaging;
using TeachingToolWpf.Imaging;

namespace TeachingToolWpf.Imaging.Tests;

public class TileWorkerLifetimeTests
{
    private sealed class Source : ITileSource
    {
        public ImageInfo Info { get; } = new("counted", "counted", 512, 32768, PixelKind.Gray8, 7);
        public ConcurrentQueue<PixelBuffer> Buffers { get; } = new();
        public int Disposals;
        public PixelBuffer Read(TileKey key, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pixels = new PixelBuffer(512, 512, PixelKind.Gray8); Buffers.Enqueue(pixels); return pixels;
        }
        public void Dispose() => Interlocked.Increment(ref Disposals);
    }
    [Fact] public void RapidReplacementUnderBackpressureDisposesEverySourceAndQueuedBuffer()
    {
        var sources = Enumerable.Range(0, 100).Select(_ => new Source()).ToArray();
        using (var worker = new TileWorker())
        {
            var generation = worker.SetSource(sources[0]);
            worker.RequestTiles(generation, Enumerable.Range(0, 64).Select(y => new TileKey(0, 0, y)).ToArray());
            Assert.True(SpinWait.SpinUntil(() => sources[0].Buffers.Count == 32, TimeSpan.FromSeconds(5)));
            foreach (var source in sources.Skip(1)) worker.SetSource(source);
        }
        Assert.All(sources, source => Assert.Equal(1, source.Disposals));
        Assert.All(sources.SelectMany(s => s.Buffers), pixels => Assert.Throws<ObjectDisposedException>(() => pixels.Bytes));
    }
}
