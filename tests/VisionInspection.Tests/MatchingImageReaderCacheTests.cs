using TeachingToolWpf.Core.Editing;
using TeachingToolWpf.Core.Imaging;
using TeachingToolWpf.Imaging.Matching;
using VisionAlgorithm.OpenSrc.PatternMatching;

namespace TeachingToolWpf.Imaging.Tests;

public sealed class MatchingImageReaderCacheTests
{
    [Theory]
    [InlineData(PixelKind.Gray8, 1)]
    [InlineData(PixelKind.Gray8, 2)]
    [InlineData(PixelKind.Gray8, 4)]
    [InlineData(PixelKind.Gray8, 8)]
    [InlineData(PixelKind.Gray8, 16)]
    [InlineData(PixelKind.Gray8, 32)]
    [InlineData(PixelKind.Gray16, 1)]
    [InlineData(PixelKind.Gray16, 2)]
    [InlineData(PixelKind.Gray16, 4)]
    [InlineData(PixelKind.Gray16, 8)]
    [InlineData(PixelKind.Gray16, 16)]
    [InlineData(PixelKind.Gray16, 32)]
    [InlineData(PixelKind.Rgba32, 1)]
    [InlineData(PixelKind.Rgba32, 2)]
    [InlineData(PixelKind.Rgba32, 4)]
    [InlineData(PixelKind.Rgba32, 8)]
    [InlineData(PixelKind.Rgba32, 16)]
    [InlineData(PixelKind.Rgba32, 32)]
    [InlineData(PixelKind.Bgra32, 1)]
    [InlineData(PixelKind.Bgra32, 2)]
    [InlineData(PixelKind.Bgra32, 4)]
    [InlineData(PixelKind.Bgra32, 8)]
    [InlineData(PixelKind.Bgra32, 16)]
    [InlineData(PixelKind.Bgra32, 32)]
    public async Task OverlappingOriginalAndReducedCropsReuseDecodedTilesWithoutChangingPixels(PixelKind kind, int factor)
    {
        var loader = new Loader(1049, 1033, kind);
        using var reader = await Open(loader);
        var original = reader.Read(480, 480, 569, 553, default);
        var reduced = reader.ReadReduced(480, 480, 569, 553, factor, default);
        var expected = InspectionPyramid.Reduce(original, factor);
        Assert.Equal(expected.Pixels.ToArray(), reduced.Pixels.ToArray());
        for (var y = 0; y < original.Height; y++)
        {
            for (var x = 0; x < original.Width; x++)
            {
                Assert.Equal(ExpectedGray(x + 480, y + 480, kind, 0), original.Pixels.Span[(y * original.Stride) + x]);
            }
        }
        Assert.Equal(9, loader.Reads);
        Assert.All(loader.Leases, lease => Assert.Throws<ObjectDisposedException>(() => lease.Bytes));
    }

    [Fact]
    public async Task CachedReadsHonorCancellationAndNeverPublishAPartiallyConvertedTile()
    {
        var loader = new Loader(512, 512, PixelKind.Rgba32);
        using var reader = await Open(loader);
        using var cancellation = new CancellationTokenSource();
        loader.AfterRead = cancellation.Cancel;
        Assert.ThrowsAny<OperationCanceledException>(() => reader.Read(0, 0, 8, 8, cancellation.Token));
        loader.AfterRead = null;
        var complete = reader.Read(0, 0, 8, 8, default);
        Assert.Equal(2, loader.Reads);
        Assert.ThrowsAny<OperationCanceledException>(() => reader.Read(0, 0, 8, 8, cancellation.Token));
        Assert.Equal(2, loader.Reads);
        Assert.Equal(ExpectedGray(7, 7, PixelKind.Rgba32, 0), complete.Pixels.Span[63]);
        Assert.All(loader.Leases, lease => Assert.Throws<ObjectDisposedException>(() => lease.Bytes));
    }

    [Fact]
    public async Task SeparateReaderForSameImageKeyCannotReusePreviousSourcePixels()
    {
        var loader = new Loader(512, 512, PixelKind.Gray8);
        var first = await Open(loader);
        var pixels = first.Read(10, 10, 8, 8, default);
        first.Dispose();
        first.Dispose();
        Assert.Throws<ObjectDisposedException>(() => first.Read(10, 10, 8, 8, default));
        Assert.Throws<ObjectDisposedException>(() => first.ReadReduced(0, 0, 8, 8, 2, default));
        Assert.Equal(1, loader.Disposals);
        loader.Offset = 90;
        using var second = await Open(loader);
        var replacement = second.Read(10, 10, 8, 8, default);
        Assert.Equal(ExpectedGray(10, 10, PixelKind.Gray8, 0), pixels.Pixels.Span[0]);
        Assert.Equal(ExpectedGray(10, 10, PixelKind.Gray8, 90), replacement.Pixels.Span[0]);
        Assert.Equal(2, loader.Reads);
    }

    [Fact]
    public async Task TileRetentionIsBoundedAndRecentlyUsedTileSurvivesEviction()
    {
        // 64 full Gray8 tiles fill the 16 MiB payload budget, without a source-sized allocation.
        var loader = new Loader(65 * 512, 512, PixelKind.Gray8);
        using var reader = await Open(loader);
        for (var column = 0; column < 64; column++)
        {
            reader.Read(column * 512, 0, 1, 1, default);
        }
        reader.Read(0, 0, 1, 1, default);
        reader.Read(64 * 512, 0, 1, 1, default);
        reader.Read(0, 0, 1, 1, default);
        Assert.Equal(65, loader.Reads);
        reader.Read(512, 0, 1, 1, default);
        Assert.Equal(66, loader.Reads);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    public async Task ReducedCropOwnsItsPixelsAfterTileEvictionAndReaderDisposal(int factor)
    {
        var loader = new Loader(65 * 512, 512, PixelKind.Gray8);
        using var reader = await Open(loader);
        var crop = reader.ReadReduced(32, 64, 160, 128, factor, default);
        var expected = crop.Pixels.ToArray();
        for (var column = 1; column <= 64; column++)
        {
            reader.Read(column * 512, 0, 1, 1, default);
        }
        reader.Dispose();
        Assert.Equal(expected, crop.Pixels.ToArray());
        Assert.Equal(65, loader.Reads);
        Assert.Equal(1, loader.Disposals);
    }

    [Fact]
    public async Task ReducedTileSurvivesOriginalEvictionAndKeepsFactorsSeparate()
    {
        var loader = new Loader(65 * 512, 512, PixelKind.Gray8);
        using var reader = await Open(loader);
        var reduced = reader.ReadReduced(0, 0, 512, 512, 4, default);
        for (var column = 1; column <= 64; column++)
        {
            reader.Read(column * 512, 0, 1, 1, default);
        }
        var repeated = reader.ReadReduced(0, 0, 512, 512, 4, default);
        Assert.Equal(65, loader.Reads);
        Assert.Equal(reduced.Pixels.ToArray(), repeated.Pixels.ToArray());
        var otherFactor = reader.ReadReduced(0, 0, 512, 512, 2, default);
        Assert.Equal(66, loader.Reads);
        Assert.Equal(256, otherFactor.Width);
        Assert.Equal(reduced.Pixels.ToArray(), InspectionPyramid.Reduce(otherFactor, 2).Pixels.ToArray());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => reader.ReadReduced(0, 0, 512, 512, 4, cancellation.Token));
        Assert.Equal(66, loader.Reads);
    }

    [Fact]
    public void ReducedTileBudgetCountsRentedBytesAndReturnsEvictedBuffers()
    {
        var cache = new MatchingReducedTileCache();
        var buffers = new List<PixelBuffer>();
        try
        {
            for (var column = 0; column < 64; column++)
            {
                // 129×256 payload는 33,024 bytes지만 실제 풀 배열은 65,536 bytes다.
                var buffer = new PixelBuffer(129, 256, PixelKind.Gray8);
                buffers.Add(buffer);
                Assert.Equal(65_536, buffer.Bytes.Length);
                cache.Add((new(0, column, 0), 2), buffer);
            }
            Assert.True(cache.TryGet((new(0, 0, 0), 2), out _));
            Assert.False(cache.TryGet((new(0, 0, 0), 4), out _));
            var replacement = new PixelBuffer(129, 256, PixelKind.Gray8);
            buffers.Add(replacement);
            cache.Add((new(0, 64, 0), 2), replacement);
            Assert.Throws<ObjectDisposedException>(() => buffers[1].Bytes);
            Assert.Equal(65_536, buffers[0].Bytes.Length);
            cache.Clear();
            cache.Clear();
            Assert.All(buffers, buffer => Assert.Throws<ObjectDisposedException>(() => buffer.Bytes));
        }
        finally
        {
            cache.Clear();
        }
    }

    [Fact]
    public async Task ReducedCacheDoesNotCrossReaderOrSourceLifetime()
    {
        var loader = new Loader(512, 512, PixelKind.Gray8);
        byte[] first;
        using (var reader = await Open(loader))
        {
            first = reader.ReadReduced(0, 0, 128, 128, 4, default).Pixels.ToArray();
        }
        loader.Offset = 90;
        using (var reader = await Open(loader))
        {
            var second = reader.ReadReduced(0, 0, 128, 128, 4, default);
            var expected = InspectionPyramid.Reduce(reader.Read(0, 0, 128, 128, default), 4);
            Assert.Equal(expected.Pixels.ToArray(), second.Pixels.ToArray());
            Assert.False(first.SequenceEqual(second.Pixels.ToArray()));
        }
        Assert.Equal(2, loader.Reads);
        Assert.Equal(2, loader.Disposals);
    }

    [Fact]
    public void EvictionAndClearReturnEveryOwnedTileBuffer()
    {
        var cache = new MatchingTileCache();
        var buffers = new List<PixelBuffer>();
        try
        {
            for (var column = 0; column < 65; column++)
            {
                var buffer = new PixelBuffer(512, 512, PixelKind.Gray8);
                buffers.Add(buffer);
                cache.Add(new(0, column, 0), buffer);
            }
            Assert.Throws<ObjectDisposedException>(() => buffers[0].Bytes);
            Assert.All(buffers.Skip(1), buffer => Assert.Equal(512 * 512, buffer.Span.Length));
            cache.Clear();
            cache.Clear();
            Assert.All(buffers, buffer => Assert.Throws<ObjectDisposedException>(() => buffer.Bytes));
        }
        finally
        {
            cache.Clear();
        }
    }

    [Fact]
    public async Task LiveReaderKeepsItsFrameLeaseWithoutReadingOrCachingTiles()
    {
        var loader = new Loader(33, 35, PixelKind.Gray16);
        var pixels = new PixelBuffer(33, 35, PixelKind.Gray16);
        pixels.Span.Fill(91);
        using var frame = new VideoFrame(1, 0, pixels);
        var reader = await MatchingImageReader.OpenAsync(new("live", "live", 33, 35), true, loader, frame.Retain, default);
        var crop = reader.ReadReduced(0, 0, 33, 35, 8, default);
        Assert.All(crop.Pixels.ToArray(), value => Assert.Equal(91, value));
        Assert.Equal(0, loader.Reads);
        reader.Dispose();
        reader.Dispose();
        Assert.Equal(0, loader.Disposals);
        Assert.Equal(91, frame.Pixels.Span[0]);
        frame.Dispose();
        Assert.Throws<ObjectDisposedException>(() => pixels.Bytes);
    }

    private static Task<MatchingImageReader> Open(Loader loader)
        => MatchingImageReader.OpenAsync(new("fixture", "fixture", loader.Width, loader.Height), false, loader, () => null, default);

    private static byte ExpectedGray(int x, int y, PixelKind kind, int offset)
    {
        var first = (byte)((x + y + offset) % 256);
        var second = (byte)((x + (y * 3) + offset) % 256);
        var third = (byte)(((x * 5) + y + offset) % 256);
        return kind switch
        {
            PixelKind.Gray8 => first,
            PixelKind.Gray16 => second,
            PixelKind.Rgba32 => (byte)(((77 * first) + (150 * second) + (29 * third) + 128) >> 8),
            _ => (byte)(((29 * first) + (150 * second) + (77 * third) + 128) >> 8)
        };
    }

    private sealed class Loader(int width, int height, PixelKind kind) : IImageLoader
    {
        public int Width => width;
        public int Height => height;
        public int Reads;
        public int Disposals;
        public int Offset;
        public Action? AfterRead;
        public List<PixelBuffer> Leases { get; } = [];

        public Task<ITileSource> LoadAsync(ImageReference reference, IProgress<string> progress, CancellationToken cancellationToken)
            => Task.FromResult<ITileSource>(new Source(this, kind, Offset));
    }

    private sealed class Source(Loader owner, PixelKind kind, int offset) : ITileSource
    {
        public ImageInfo Info { get; } = new("fixture", "fixture", owner.Width, owner.Height, kind, 1);

        public PixelBuffer Read(TileKey key, CancellationToken cancellationToken)
        {
            Assert.Equal(0, key.Level);
            owner.Reads++;
            var left = key.X * 512;
            var top = key.Y * 512;
            var pixels = new PixelBuffer(Math.Min(512, owner.Width - left), Math.Min(512, owner.Height - top), kind);
            owner.Leases.Add(pixels);
            for (var y = 0; y < pixels.Height; y++)
            {
                for (var x = 0; x < pixels.Width; x++)
                {
                    var index = (y * pixels.Stride) + (x * Info.BytesPerPixel);
                    pixels.Span[index] = (byte)((left + x + top + y + offset) % 256);
                    if (Info.BytesPerPixel > 1)
                    {
                        pixels.Span[index + 1] = (byte)((left + x + ((top + y) * 3) + offset) % 256);
                    }
                    if (Info.BytesPerPixel > 2)
                    {
                        pixels.Span[index + 2] = (byte)((((left + x) * 5) + top + y + offset) % 256);
                        pixels.Span[index + 3] = 255;
                    }
                }
            }
            owner.AfterRead?.Invoke();
            return pixels;
        }

        public void Dispose() => owner.Disposals++;
    }
}
