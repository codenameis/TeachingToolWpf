using VisionImaging.Abstractions;

// 평가 전용: 동일 원본 타일을 한 번만 생성한다. 제품에는 영상/검출 결과 캐시를 추가하지 않는다.
internal sealed class PrefilledLoader(ImageInfo info, byte[][] tiles) : IImageLoader
{
    public static async Task<PrefilledLoader> CreateAsync(IImageLoader loader, ImageReference image)
    {
        using var source = await loader.LoadAsync(image, new Progress<string>(), default);
        if (source.Info.Pixels != PixelKind.Gray8)
        {
            throw new ArgumentException("Prefill requires Gray8 input.");
        }
        var columns = (image.Width + 511) / 512;
        var rows = (image.Height + 511) / 512;
        var tiles = new byte[columns * rows][];
        for (var y = 0; y < rows; y++)
        {
            for (var x = 0; x < columns; x++)
            {
                using var tile = source.Read(new(0, x, y), default);
                if (tile.Kind != PixelKind.Gray8)
                {
                    throw new ArgumentException("Prefill requires Gray8 tiles without display conversion.");
                }
                tiles[(y * columns) + x] = tile.Span.ToArray();
            }
        }
        return new(source.Info, tiles);
    }

    public Task<ITileSource> LoadAsync(ImageReference image, IProgress<string> progress, CancellationToken token)
        => Task.FromResult<ITileSource>(new Source(info, tiles));

    private sealed class Source(ImageInfo info, byte[][] tiles) : ITileSource
    {
        public ImageInfo Info => info;

        public PixelBuffer Read(TileKey key, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var tile = new PixelBuffer(Math.Min(512, info.Width - (key.X * 512)),
                Math.Min(512, info.Height - (key.Y * 512)), PixelKind.Gray8);
            tiles[(key.Y * ((info.Width + 511) / 512)) + key.X].AsSpan().CopyTo(tile.Span);
            return tile;
        }

        public void Dispose()
        {
        }
    }
}
