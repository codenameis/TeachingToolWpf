using TeachingToolWpf.Imaging.Matching;
using VisionAlgorithm.OpenSrc.PatternMatching;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Imaging.Tests;

public sealed class TemplateMatchLimitTests
{
    [Theory]
    [InlineData(2, true)]
    [InlineData(3, false)]
    public async Task OutputLimitIsReportedAfterCollectingAllSearchWindows(int maximumMatches, bool expectedLimitExceeded)
    {
        const int Width = 5000;
        const int Height = 40;
        var pattern = new byte[24 * 20];
        new Random(647).NextBytes(pattern);
        var scene = new byte[Width * Height];
        foreach (var left in new[] { 10, 2100, 4200 })
        {
            for (var row = 0; row < 20; row++)
            {
                pattern.AsSpan(row * 24, 24).CopyTo(scene.AsSpan(((10 + row) * Width) + left));
            }
        }
        var model = CorrelationTemplate.Train(new(pattern, 24, 20, 24), new(ReadOnlyMemory<byte>.Empty, new(12, 10)));
        var service = new TemplateMatchingService(new Loader(scene, Width, Height), () => null);

        var output = await service.SearchAsync(new("fixture", "fixture", Width, Height), false, model, null, new()
        {
            MinimumScore = .99,
            MaximumMatches = maximumMatches
        }, default);

        Assert.Equal(3, output.Windows);
        Assert.False(output.Truncated);
        Assert.Equal(maximumMatches, output.Matches.Length);
        Assert.Equal(expectedLimitExceeded, output.MatchLimitExceeded);
    }

    private sealed class Loader(byte[] pixels, int width, int height) : IImageLoader
    {
        public Task<ITileSource> LoadAsync(ImageReference reference, IProgress<string> progress, CancellationToken cancellationToken)
            => Task.FromResult<ITileSource>(new Source(pixels, width, height));
    }

    private sealed class Source(byte[] pixels, int width, int height) : ITileSource
    {
        public ImageInfo Info { get; } = new("fixture", "fixture", width, height, PixelKind.Gray8, 1);

        public PixelBuffer Read(TileKey key, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(0, key.Level);
            var left = key.X * 512;
            var top = key.Y * 512;
            var tileWidth = Math.Min(512, width - left);
            var tileHeight = Math.Min(512, height - top);
            var tile = new PixelBuffer(tileWidth, tileHeight, PixelKind.Gray8);
            for (var row = 0; row < tileHeight; row++)
            {
                pixels.AsSpan(((top + row) * width) + left, tileWidth).CopyTo(tile.Span.Slice(row * tile.Stride, tileWidth));
            }
            return tile;
        }

        public void Dispose()
        {
        }
    }
}
