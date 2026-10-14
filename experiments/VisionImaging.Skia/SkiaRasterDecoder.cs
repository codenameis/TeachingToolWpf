using SkiaSharp;
using VisionImaging.Abstractions;
using VisionImaging.Pyramid;

namespace VisionImaging.Skia;

/// <summary>PNG 전체 비트맵/JPEG 순차 행 디코드. TIFF는 호스트가 기존 디코더에 위임한다.</summary>
public sealed class SkiaRasterDecoder : IRasterDecoder
{
    public string CacheIdentity => "SkiaSharp-4.153.1-jpeg-scanline-v2";

    public IRasterReader Open(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // Skia의 8비트 표시 변환으로 검사 원본의 16비트 명암을 잃지 않도록 명시적으로 거부한다.
        using (var input = File.OpenRead(path))
        {
            Span<byte> header = stackalloc byte[26];
            var read = input.Read(header);
            if (read >= 3 && header[0] == 255 && header[1] == 216 && header[2] == 255)
                return JpegScanlineReader.Open(path, token);
            if (read >= 26 && header[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) && header[24] == 16)
                throw new NotSupportedException("Skia 비교 경로는 16-bit PNG를 지원하지 않습니다. 기존 스트리밍 로더를 사용하세요.");
        }
        using var codec = SKCodec.Create(path) ?? throw new InvalidDataException("Skia가 이미지 헤더를 읽지 못했습니다.");
        if (codec.EncodedFormat is not (SKEncodedImageFormat.Png or SKEncodedImageFormat.Jpeg))
            throw new NotSupportedException("Skia 비교 경로는 PNG/JPEG만 지원합니다.");
        var source = codec.Info;
        var gray = source.ColorType == SKColorType.Gray8 && source.AlphaType == SKAlphaType.Opaque;
        var info = new SKImageInfo(source.Width, source.Height, gray ? SKColorType.Gray8 : SKColorType.Rgba8888,
            gray ? SKAlphaType.Opaque : SKAlphaType.Unpremul);
        if (info.Width <= 0 || info.Height <= 0 || checked((long)info.RowBytes * info.Height) > 512L * 1024 * 1024)
            throw new NotSupportedException("Skia 비교용 원본 비트맵 한도는 512 MiB입니다.");
        var bitmap = new SKBitmap(info);
        try
        {
            if (bitmap.GetPixels() == IntPtr.Zero) throw new OutOfMemoryException("Skia bitmap allocation failed.");
            var result = codec.GetPixels(info, bitmap.GetPixels());
            token.ThrowIfCancellationRequested();
            if (result != SKCodecResult.Success) throw new InvalidDataException($"Skia decode failed: {result}");
            return new Reader(bitmap, gray ? PixelKind.Gray8 : PixelKind.Rgba32);
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    private sealed class Reader(SKBitmap bitmap, PixelKind kind) : IRasterReader
    {
        private SKBitmap? pixels = bitmap;
        private int nextRow;
        public int Width { get; } = bitmap.Width;
        public int Height { get; } = bitmap.Height;
        public PixelKind Kind { get; } = kind;

        public unsafe void ReadRows(int top, int height, Span<byte> destination, CancellationToken token)
        {
            ObjectDisposedException.ThrowIf(pixels is null, this);
            token.ThrowIfCancellationRequested();
            if (top != nextRow || height <= 0 || height > Height - top) throw new ArgumentOutOfRangeException(nameof(top));
            var stride = checked(Width * (Kind == PixelKind.Gray8 ? 1 : 4));
            var length = checked(stride * height);
            if (destination.Length < length) throw new ArgumentException("Destination is too small.", nameof(destination));
            for (var row = 0; row < height; row++)
            {
                token.ThrowIfCancellationRequested();
                new ReadOnlySpan<byte>((byte*)pixels.GetPixels() + (long)(top + row) * pixels.RowBytes, stride)
                    .CopyTo(destination.Slice(row * stride, stride));
            }
            nextRow += height;
        }

        public void Dispose()
        {
            pixels?.Dispose();
            pixels = null;
        }
    }
}
