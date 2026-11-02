using CpuMatching.Core;
using TeachingToolWpf.Imaging.Matching;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Imaging.Tests;

public sealed class CpuWindowedInputTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(0, 4)]
    [InlineData(3, 2)]
    [InlineData(7, 4)]
    [InlineData(3, 16)]
    public async Task WindowedReaderKeepsOriginalAndOddOriginPyramidPixels(int origin, int factor)
    {
        var loader = new Source(1109, 1083);
        using var reader = await MatchingImageReader.OpenAsync(new("test", "test", 1109, 1083), false, loader, () => null, default);
        var source = new CpuMatchingImageSource(reader, new(origin, origin, 1090, 1070));
        var region = new SearchRegion(0, 0, 1024, 1024);
        var raw = reader.Read(origin, origin, 1024, 1024, default);
        var expected = new GrayImage(1024, 1024, raw.Pixels.Span);
        for (var step = 1; step < factor; step *= 2)
        {
            expected = expected.Half();
        }
        var actual = source.Read(region, factor, default);
        Assert.Equal(expected.Pixels.ToArray(), actual.Pixels.ToArray());
        Assert.True(source.ReadMilliseconds > 0);
    }

    [Fact]
    public async Task ServiceAcceptsKen32DimensionsKeepsLastPixelCoordinatesAndDisposesReader()
    {
        var loader = new Source(17_280, 30_720);
        var service = new CpuTemplateMatchingService(loader, () => null);
        var template = new CpuTemplate(CpuTemplateMethod.Line2D, new WindowedMatcher(), new(new(20, 20), 20, 20, 0));
        var output = await service.SearchAsync(new("large", "large", loader.Info.Width, loader.Info.Height), false,
            template, null, new(), default);
        var match = Assert.Single(output.Result.Matches);
        Assert.Equal(17_270.5, match.X);
        Assert.Equal(30_710.5, match.Y);
        Assert.True(loader.Disposed);
        Assert.Contains(new TileKey(0, 33, 59), loader.Keys);
    }

    private sealed class WindowedMatcher : ITemplateMatcher
    {
        public SearchResult Find(GrayImage image, SearchOptions? options = null, CancellationToken cancellationToken = default,
            Func<double, double, bool>? acceptCenter = null, ICoarseScoreAccelerator? accelerator = null)
            => throw new InvalidOperationException("Large input must not allocate a contiguous image.");

        public SearchResult FindWindowed(IGrayImageSource image, SearchOptions? options = null, CancellationToken cancellationToken = default,
            Func<double, double, bool>? acceptCenter = null, ICoarseScoreAccelerator? accelerator = null)
        {
            Assert.Equal(17_280, image.Width);
            Assert.Equal(30_720, image.Height);
            Assert.Null(acceptCenter);
            var crop = image.Read(new(image.Width - 16, image.Height - 16, 16, 16), 1, cancellationToken);
            Assert.Equal(Value(image.Width - 1, image.Height - 1), crop[15, 15]);
            return new([new(17_270, 30_710, 0, 1, .9)], false, false, 1);
        }
    }

    private static byte Value(int x, int y) => (byte)((x * 37) + (y * 53));

    private sealed class Source(int width, int height) : IImageLoader, ITileSource
    {
        public ImageInfo Info { get; } = new("test", "test", width, height, PixelKind.Gray8, 1);
        public HashSet<TileKey> Keys { get; } = [];
        public bool Disposed
        {
            get; private set;
        }
        public Task<ITileSource> LoadAsync(ImageReference image, IProgress<string> progress, CancellationToken cancellationToken)
            => Task.FromResult<ITileSource>(this);
        public PixelBuffer Read(TileKey key, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Keys.Add(key);
            var tile = new PixelBuffer(Math.Min(512, width - (key.X * 512)), Math.Min(512, height - (key.Y * 512)), PixelKind.Gray8);
            for (var y = 0; y < tile.Height; y++)
            {
                for (var x = 0; x < tile.Width; x++)
                {
                    tile.Span[(y * tile.Stride) + x] = Value((key.X * 512) + x, (key.Y * 512) + y);
                }
            }
            return tile;
        }
        public void Dispose() => Disposed = true;
    }
}
