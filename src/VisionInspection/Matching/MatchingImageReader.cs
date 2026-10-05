using System.Runtime.CompilerServices;
using TeachingToolWpf.Core.Editing;
using TeachingToolWpf.Core.Imaging;
using VisionAlgorithm;
using VisionAlgorithm.OpenSrc.PatternMatching;

namespace TeachingToolWpf.Imaging.Matching;

/// <summary>표시 소스와 별도 수명을 가진 원본 해상도 읽기. 한 worker가 읽기와 해제를 소유한다.</summary>
internal sealed class MatchingImageReader : IDisposable
{
    private readonly ITileSource? _source;
    private readonly VideoFrame? _frame;
    private readonly MatchingTileCache _tiles = new();
    private readonly MatchingReducedTileCache _reducedTiles = new();
    private bool _disposed;

    public bool PreferDirectGray
    {
        get; set;
    }

    private MatchingImageReader(ITileSource? source, VideoFrame? frame)
    {
        _source = source;
        _frame = frame;
    }

    public static async Task<MatchingImageReader> OpenAsync(ImageReference image, bool live, IImageLoader loader,
        Func<VideoFrame?> retainFrame, CancellationToken token)
    {
        if (live)
        {
            var frame = retainFrame() ?? throw new InvalidOperationException("검사할 원본 프레임이 없습니다.");
            return new(null, frame);
        }
        return new(await loader.LoadAsync(image, new Progress<string>(), token).ConfigureAwait(false), null);
    }

    public GrayImage Read(int left, int top, int width, int height, CancellationToken token,
        int maximumPixels = VisionAlgorithm.OpenSrc.PatternMatching.PreparedSearchImage.MaximumPixels)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        token.ThrowIfCancellationRequested();
        if ((long)width * height > maximumPixels || maximumPixels > VisionAlgorithm.OpenSrc.PatternMatching.CorrelationTemplate.MaximumSearchPixels)
        {
            throw new ArgumentException($"학습/검색 창이 {maximumPixels:N0} pixel 작업 예산을 초과했습니다.");
        }
        var bytes = new byte[checked(width * height)];
        if (_frame is { } frame)
        {
            Copy(frame.Pixels, 0, 0, left, top, width, height, bytes, token);
        }
        else
        {
            for (var ty = top / 512; ty <= (top + height - 1) / 512; ty++)
            {
                for (var tx = left / 512; tx <= (left + width - 1) / 512; tx++)
                {
                    token.ThrowIfCancellationRequested();
                    var tile = ReadTile(new(0, tx, ty), token);
                    var copyLeft = Math.Max(left, tx * 512);
                    var copyRight = Math.Min(left + width, (tx * 512) + tile.Width);
                    for (var y = Math.Max(top, ty * 512); y < Math.Min(top + height, (ty * 512) + tile.Height); y++)
                    {
                        token.ThrowIfCancellationRequested();
                        tile.Span.Slice(((y - (ty * 512)) * tile.Stride) + copyLeft - (tx * 512), copyRight - copyLeft)
                            .CopyTo(bytes.AsSpan(((y - top) * width) + copyLeft - left));
                    }
                }
            }
        }
        return new(bytes, width, height, width);
    }

    public GrayImage ReadReduced(int left, int top, int width, int height, int factor, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        token.ThrowIfCancellationRequested();
        if (factor is < 1 or > 32 || (factor & (factor - 1)) != 0 || left % factor != 0 || top % factor != 0 ||
            (long)(width / factor) * (height / factor) > PreparedSearchImage.MaximumPixels)
        {
            throw new ArgumentException("축소 검사 창의 정렬 또는 메모리 예산이 잘못되었습니다.");
        }
        if (factor == 1)
        {
            return Read(left, top, width, height, token);
        }
        var outputWidth = width / factor;
        var outputHeight = height / factor;
        var output = new byte[checked(outputWidth * outputHeight)];
        // 512는 모든 허용 factor의 배수다. 각 원본 타일을 한 번 읽고 동일한 피라미드를 만든다.
        for (var y = top; y < top + (outputHeight * factor);)
        {
            var bottom = Math.Min(top + (outputHeight * factor), ((y / 512) + 1) * 512);
            for (var x = left; x < left + (outputWidth * factor);)
            {
                var right = Math.Min(left + (outputWidth * factor), ((x / 512) + 1) * 512);
                // 정렬된 crop의 반복 2×2 평균은 전체 타일을 줄인 뒤 같은 부분을 읽는 것과 같다.
                var reduced = ReadReducedTile(new(0, x / 512, y / 512), factor, token);
                var reducedLeft = x % 512 / factor;
                var reducedTop = y % 512 / factor;
                var reducedWidth = (right - x) / factor;
                var reducedHeight = (bottom - y) / factor;
                for (var row = 0; row < reducedHeight; row++)
                {
                    token.ThrowIfCancellationRequested();
                    reduced.Span.Slice(((reducedTop + row) * reduced.Stride) + reducedLeft, reducedWidth).CopyTo(
                        output.AsSpan(((((y - top) / factor) + row) * outputWidth) + ((x - left) / factor), reducedWidth));
                }
                x = right;
            }
            y = bottom;
        }
        return new(output, outputWidth, outputHeight, outputWidth);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private PixelBuffer ReadReducedTile(TileKey key, int factor, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_reducedTiles.TryGet((key, factor), out var cached))
        {
            return cached;
        }
        GrayImage original;
        if (_frame is { } frame)
        {
            var left = key.X * 512;
            var top = key.Y * 512;
            original = Read(left, top, Math.Min(512, frame.Pixels.Width - left),
                Math.Min(512, frame.Pixels.Height - top), token);
        }
        else
        {
            var tile = ReadTile(key, token);
            original = new(tile.Bytes.AsMemory(0, tile.Length), tile.Width, tile.Height, tile.Stride);
        }
        // 원본 view는 다음 원본 타일 요청 전에 소비한다. 축소 cache도 독립 소유 버퍼만 보관한다.
        var image = InspectionTileReducer.Reduce(original, factor, token);
        try
        {
            token.ThrowIfCancellationRequested();
            _reducedTiles.Add((key, factor), image);
            return image;
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    private PixelBuffer ReadTile(TileKey key, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_tiles.TryGet(key, out var cached))
        {
            return cached;
        }
        if (PreferDirectGray && _source is IOriginalGrayTileSource direct)
        {
            var gray = direct.ReadOriginalGray8(key.X, key.Y, token);
            try
            {
                token.ThrowIfCancellationRequested();
                if (gray.Kind != PixelKind.Gray8 || gray.Width != Math.Min(512, _source.Info.Width - (key.X * 512)) ||
                    gray.Height != Math.Min(512, _source.Info.Height - (key.Y * 512)))
                {
                    throw new InvalidOperationException("원본 Gray8 공급자가 타일 규격과 다른 버퍼를 반환했습니다.");
                }
                _tiles.Add(key, gray);
                return gray;
            }
            catch
            {
                gray.Dispose();
                throw;
            }
        }
        using var tile = _source!.Read(key, token);
        var image = new PixelBuffer(tile.Width, tile.Height, PixelKind.Gray8);
        try
        {
            Copy(tile, 0, 0, 0, 0, tile.Width, tile.Height, image.Bytes, token);
            token.ThrowIfCancellationRequested();
            // 완성된 버퍼만 캐시에 넘긴다. crop은 복사본이므로 퇴출 시 즉시 풀로 반환할 수 있다.
            _tiles.Add(key, image);
            return image;
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Copy(PixelBuffer pixels, int originX, int originY, int left, int top, int width, int height,
        byte[] output, CancellationToken token)
    {
        var input = pixels.Span;
        var bytesPerPixel = pixels.Kind == PixelKind.Gray8 ? 1 : pixels.Kind == PixelKind.Gray16 ? 2 : 4;
        var copyLeft = Math.Max(left, originX);
        var copyWidth = Math.Max(0, Math.Min(left + width, originX + pixels.Width) - copyLeft);
        if (copyWidth == 0)
        {
            return;
        }
        for (var y = Math.Max(top, originY); y < Math.Min(top + height, originY + pixels.Height); y++)
        {
            token.ThrowIfCancellationRequested();
            var source = input.Slice(((y - originY) * pixels.Stride) + ((copyLeft - originX) * bytesPerPixel), copyWidth * bytesPerPixel);
            var destination = output.AsSpan(((y - top) * width) + copyLeft - left, copyWidth);
            InspectionGrayConverter.ConvertRow(source, destination, pixels.Kind);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _tiles.Clear();
        _reducedTiles.Clear();
        _source?.Dispose();
        _frame?.Dispose();
    }
}
