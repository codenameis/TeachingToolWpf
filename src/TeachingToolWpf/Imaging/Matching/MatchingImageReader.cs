using TeachingToolWpf.Core.Editing;
using TeachingToolWpf.Core.Imaging;
using VisionAlgorithm;

namespace TeachingToolWpf.Imaging.Matching;

/// <summary>표시 소스와 별도 수명을 가진 원본 해상도 읽기. 한 worker가 읽기와 해제를 소유한다.</summary>
internal sealed class MatchingImageReader : IDisposable
{
    private readonly ITileSource? _source;
    private readonly VideoFrame? _frame;

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

    public GrayImage Read(int left, int top, int width, int height, CancellationToken token)
    {
        if ((long)width * height > VisionAlgorithm.OpenSrc.PatternMatching.PreparedSearchImage.MaximumPixels)
        {
            throw new ArgumentException("학습/검색 창이 2 Mpx 작업 예산을 초과했습니다. 더 작은 학습 ROI를 사용하십시오.");
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
                    using var tile = _source!.Read(new(0, tx, ty), token);
                    Copy(tile, tx * 512, ty * 512, left, top, width, height, bytes, token);
                }
            }
        }
        return new(bytes, width, height, width);
    }

    private static void Copy(PixelBuffer pixels, int originX, int originY, int left, int top, int width, int height,
        byte[] output, CancellationToken token)
    {
        var input = pixels.Span;
        var bytesPerPixel = pixels.Kind == PixelKind.Gray8 ? 1 : pixels.Kind == PixelKind.Gray16 ? 2 : 4;
        for (var y = Math.Max(top, originY); y < Math.Min(top + height, originY + pixels.Height); y++)
        {
            token.ThrowIfCancellationRequested();
            for (var x = Math.Max(left, originX); x < Math.Min(left + width, originX + pixels.Width); x++)
            {
                var source = ((y - originY) * pixels.Stride) + ((x - originX) * bytesPerPixel);
                // Gray16은 고정 상위 8비트, RGB는 고정 정수 luminance. 프레임별 auto contrast는 하지 않는다.
                output[((y - top) * width) + x - left] = pixels.Kind switch
                {
                    PixelKind.Gray8 => input[source],
                    PixelKind.Gray16 => input[source + 1],
                    PixelKind.Rgba32 => (byte)(((77 * input[source]) + (150 * input[source + 1]) + (29 * input[source + 2]) + 128) >> 8),
                    PixelKind.Bgra32 => (byte)(((29 * input[source]) + (150 * input[source + 1]) + (77 * input[source + 2]) + 128) >> 8),
                    _ => throw new ArgumentOutOfRangeException(nameof(pixels))
                };
            }
        }
    }

    public void Dispose()
    {
        _source?.Dispose();
        _frame?.Dispose();
    }
}
