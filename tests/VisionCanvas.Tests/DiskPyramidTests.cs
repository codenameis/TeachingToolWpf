using NetVips;
using VisionImaging;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Imaging.Tests;

public sealed class DiskPyramidTests : IDisposable
{
    private readonly string _directory = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));

    public DiskPyramidTests()
    {
        Directory.CreateDirectory(_directory);
        Cache.MaxMem = 64UL * 1024 * 1024;
    }

    [Fact]
    public void Actual24KGray16FilePreservesPixelValuesAndEdgeTiles()
    {
        var path = Path.Combine(_directory, "24k.tif");
        using (var black = Image.Black(24576, 513))
        using (var value = black + 12345)
        using (var gray = value.Cast(Enums.BandFormat.Ushort))
        {
            gray.WriteToFile(path);
        }

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
        using (var black = Image.Black(600, 3))
        using (var gray = (black + 73).Cast(Enums.BandFormat.Uchar))
        {
            gray.WriteToFile(path);
        }
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
        using (var image = Image.Black(1024, 1024))
        {
            image.WriteToFile(path);
        }

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => DiskPyramidSource.Open(path, Path.Combine(_directory, "cache"), new Progress<string>(), cancellation.Token));
        Assert.False(Directory.Exists(Path.Combine(_directory, "cache")));
    }

    [Theory]
    [InlineData("png", 24576)]
    [InlineData("jpg", 24576)]
    [InlineData("webp", 8192)]
    public void ColorFilesPreserveChannelsAndDimensions(string extension, int width)
    {
        var path = Path.Combine(_directory, "color." + extension);
        using (var black = Image.Black(width, 9, bands: 3))
        using (var color = (black + new[] { 210d, 90d, 30d }).Cast(Enums.BandFormat.Uchar))
        {
            color.WriteToFile(path);
        }

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
