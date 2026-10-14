using VisionImaging.Fixtures;
using VisionImaging;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Imaging.Tests;

public sealed class DiskTileRegionTests : IDisposable
{
    private readonly string _directory = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RegionReadsPreserveOriginalValuesAndAllowSequentialWorkerChanges(bool gray16)
    {
        Directory.CreateDirectory(_directory);
        const int Width = 1033;
        const int Height = 1029;
        var bytesPerPixel = gray16 ? 2 : 4;
        var bytes = new byte[Width * Height * bytesPerPixel];
        new Random(541).NextBytes(bytes);
        if (!gray16)
        {
            for (var index = 3; index < bytes.Length; index += 4)
            {
                bytes[index] = 255;
            }
        }
        var path = Path.Combine(_directory, "pixels.tif");
        ImageFixtureWriter.Write(path, Width, Height, gray16 ? 1 : 4, gray16 ? 16 : 8, bytes);
        using var source = DiskPyramidSource.Open(path, Path.Combine(_directory, "cache"), new Progress<string>(), default);
        for (var row = 0; row < 3; row++)
        {
            for (var column = 0; column < 3; column++)
            {
                var key = new TileKey(0, column, row);
                using var tile = await Task.Factory.StartNew(() => source.Read(key, default),
                    CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                Assert.Equal(gray16 ? PixelKind.Gray16 : PixelKind.Rgba32, tile.Kind);
                for (var y = 0; y < tile.Height; y++)
                {
                    var expected = bytes.AsSpan(((((row * 512) + y) * Width) + (column * 512)) * bytesPerPixel,
                        tile.Width * bytesPerPixel);
                    Assert.True(expected.SequenceEqual(tile.Span.Slice(y * tile.Stride, tile.Stride)));
                }
            }
        }
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => source.Read(new(0, 0, 0), cancellation.Token));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }
}
