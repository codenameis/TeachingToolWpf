using VisionImaging.Fixtures;
using VisionImaging;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Imaging.Tests;

public sealed class DiskPyramidTests : IDisposable
{
    private readonly string _directory = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));

    public DiskPyramidTests()
    {
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public void Actual24KGray16FilePreservesPixelValuesAndEdgeTiles()
    {
        var path = Path.Combine(_directory, "24k.tif");
        ImageFixtureWriter.Constant(path, 24576, 513, 16, 12345);

        using var source = DiskPyramidSource.Open(path, Path.Combine(_directory, "cache"), new Progress<string>(), CancellationToken.None);
        Assert.Equal(24576, source.Info.Width);
        Assert.Equal(PixelKind.Gray16, source.Info.Pixels);
        using var tile = source.Read(new(0, 47, 1), CancellationToken.None);
        Assert.Equal(512, tile.Width);
        Assert.Equal(1, tile.Height);
        Assert.Equal((ushort)12345, BitConverter.ToUInt16(tile.Bytes));
        for (var level = 1; level < source.Info.Levels; level++)
        {
            using var reduced = source.Read(new(level, 0, 0), CancellationToken.None);
            Assert.InRange(BitConverter.ToUInt16(reduced.Bytes), 12344, 12346);
        }
    }

    [Fact]
    public async Task RelativeCacheDirectoryImportsAndReopensOriginalPixels()
    {
        var path = Path.Combine(_directory, "relative-cache.png");
        ImageFixtureWriter.Constant(path, 600, 3, 8, 73);
        var cacheRoot = Path.Combine(_directory, "cache");
        var relativeCache = Path.GetRelativePath(Environment.CurrentDirectory, cacheRoot);
        Assert.False(Path.IsPathRooted(relativeCache));
        var loader = new ImageLoader(relativeCache);
        var reference = new ImageReference(path, "relative-cache", 600, 3);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var source = await loader.LoadAsync(reference, new Progress<string>(), CancellationToken.None);
            Assert.Equal(path, source.Info.Key);
            Assert.Equal(PixelKind.Gray8, source.Info.Pixels);
            Assert.Equal(600, source.Info.Width);
            Assert.Equal(3, source.Info.Height);
            using var tile = source.Read(new(0, 1, 0), CancellationToken.None);
            Assert.Equal(88, tile.Width);
            Assert.Equal(3, tile.Height);
            Assert.All(tile.Bytes.Take(tile.Length), pixel => Assert.Equal((byte)73, pixel));
        }
        Assert.Single(Directory.GetDirectories(cacheRoot));
    }

    [Fact]
    public void CancellationDoesNotPublishPartialCache()
    {
        var path = Path.Combine(_directory, "sample.png");
        ImageFixtureWriter.Constant(path, 1024, 1024, 8, 0);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => DiskPyramidSource.Open(path, Path.Combine(_directory, "cache"), new Progress<string>(), cancellation.Token));
        Assert.False(Directory.Exists(Path.Combine(_directory, "cache")));
    }

    [Theory]
    [InlineData(8, 1)]
    [InlineData(16, 1)]
    [InlineData(8, 513)]
    [InlineData(16, 513)]
    public void BigTiffFixturePreservesOriginalRows(int bits, int height)
    {
        var path = Path.Combine(_directory, "rows.tif");
        ImageFixtureWriter.WriteBigTiff(path, 517, height, bits, (y, row) =>
        {
            for (var x = 0; x < 517; x++)
            {
                var value = (x + y) % 256;
                if (bits == 8) row[x] = (byte)value;
                else System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(row.AsSpan(x * 2), (ushort)(value * 257));
            }
        });
        using var source = DiskPyramidSource.Open(path, Path.Combine(_directory, "cache"), new Progress<string>(), CancellationToken.None);
        using var edge = source.Read(new(0, 1, (height - 1) / 512), CancellationToken.None);
        Assert.Equal(5, edge.Width);
        for (var y = 0; y < edge.Height; y++)
        for (var x = 0; x < edge.Width; x++)
        {
            var expected = (512 + x + (height - 1) / 512 * 512 + y) % 256;
            var offset = (y * edge.Width + x) * (bits / 8);
            Assert.Equal(expected * (bits == 8 ? 1 : 257), bits == 8 ? edge.Bytes[offset] : BitConverter.ToUInt16(edge.Bytes, offset));
        }
    }

    [Theory]

    [InlineData("bmp")]
    public void UnsupportedFilesFailWithoutPublishingCache(string extension)
    {
        var path = Path.Combine(_directory, "unsupported." + extension);
        ImageFixtureWriter.Constant(path, 16, 16, 8, 12, 34, 56);
        var cache = Path.Combine(_directory, "cache");
        Assert.Throws<NotSupportedException>(() => DiskPyramidSource.Open(path, cache, new Progress<string>(), CancellationToken.None));
        Assert.Empty(Directory.GetFiles(cache, "image.json", SearchOption.AllDirectories));
    }

    [Fact]
    public void LegacyCacheIsPreservedDuringImport()
    {
        var cache = Path.Combine(_directory, "cache");
        var legacy = Path.Combine(cache, "legacy");
        Directory.CreateDirectory(legacy);
        var sentinel = Path.Combine(legacy, "0.v");
        File.WriteAllText(sentinel, "preserved legacy cache");
        var path = Path.Combine(_directory, "sample.png");
        ImageFixtureWriter.Constant(path, 16, 16, 8, 73);
        using var source = DiskPyramidSource.Open(path, cache, new Progress<string>(), CancellationToken.None);
        Assert.Equal("preserved legacy cache", File.ReadAllText(sentinel));
    }

    [Theory]
    [InlineData("png", 24576)]
    [InlineData("jpg", 24576)]
    [InlineData("webp", 8192)]

    public void ColorFilesPreserveChannelsAndDimensions(string extension, int width)
    {
        var path = Path.Combine(_directory, "color." + extension);
        ImageFixtureWriter.Constant(path, width, 9, 8, 210, 90, 30);

        using var source = DiskPyramidSource.Open(path, Path.Combine(_directory, "cache"), new Progress<string>(), CancellationToken.None);
        Assert.Equal(PixelKind.Rgba32, source.Info.Pixels);
        Assert.Equal(width, source.Info.Width);
        using var edge = source.Read(new(0, (width / 512) - 1, 0), CancellationToken.None);
        Assert.Equal(9, edge.Height);
        Assert.InRange(edge.Bytes[0], 205, 215);
        Assert.InRange(edge.Bytes[1], 85, 95);
        Assert.InRange(edge.Bytes[2], 25, 35);
        Assert.Equal(255, edge.Bytes[3]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }
}
