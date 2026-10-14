using SkiaSharp;
using VisionImaging.Abstractions;
using VisionImaging.Pyramid;

namespace VisionImaging.Skia;

/// <summary>호출자의 행 버퍼에 직접 디코드한다. native 소비 후 실패한 reader는 폐기한다.</summary>
internal sealed class JpegScanlineReader : IRasterReader
{
    private SKCodec? codec;
    private readonly int stride;
    private int nextRow;
    public int Width { get; }
    public int Height { get; }
    public PixelKind Kind { get; }

    private JpegScanlineReader(SKCodec codec, SKImageInfo info, bool gray)
    {
        this.codec = codec;
        Width = info.Width;
        Height = info.Height;
        stride = info.RowBytes;
        Kind = gray ? PixelKind.Gray8 : PixelKind.Rgba32;
    }

    internal static JpegScanlineReader Open(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var codec = SKCodec.Create(path) ?? throw new InvalidDataException("Skia JPEG header is invalid.");
        try
        {
            if (codec.EncodedFormat != SKEncodedImageFormat.Jpeg)
                throw new InvalidDataException("Expected JPEG input.");
            var source = codec.Info;
            var gray = source.ColorType == SKColorType.Gray8;
            var stride = checked((long)source.Width * (gray ? 1 : 4));
            // 이전 비교의 입력 크기 제한을 유지한다. 내부 JPEG 계수 메모리 상한은 아니다.
            if (source.Width <= 0 || source.Height <= 0 || stride * source.Height > 512L * 1024 * 1024)
                throw new NotSupportedException("Skia JPEG 비교용 원본 크기 한도는512 MiB입니다.");
            var info = new SKImageInfo(source.Width, source.Height,
                gray ? SKColorType.Gray8 : SKColorType.Rgba8888, SKAlphaType.Opaque);
            var result = codec.StartScanlineDecode(info);
            token.ThrowIfCancellationRequested();
            if (result != SKCodecResult.Success || codec.ScanlineOrder != SKCodecScanlineOrder.TopDown)
                throw new InvalidDataException($"Skia JPEG scanline initialization failed: {result}, {codec.ScanlineOrder}");
            return new JpegScanlineReader(codec, info, gray);
        }
        catch
        {
            codec.Dispose();
            throw;
        }
    }

    public unsafe void ReadRows(int top, int height, Span<byte> destination, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(codec is null, this);
        token.ThrowIfCancellationRequested();
        if (top != nextRow || height <= 0 || height > Height - top)
            throw new ArgumentOutOfRangeException(nameof(top));
        if (destination.Length < checked(stride * height))
            throw new ArgumentException("Destination is too small.", nameof(destination));
        try
        {
            fixed (byte* output = destination)
            {
                for (var row = 0; row < height;)
                {
                    token.ThrowIfCancellationRequested();
                    var count = Math.Min(64, height - row);
                    var decoded = codec.GetScanlines((IntPtr)(output + (long)row * stride), count, stride);
                    token.ThrowIfCancellationRequested();
                    if (decoded != count)
                        throw new InvalidDataException($"Incomplete JPEG: requested {count} rows, decoded {decoded}.");
                    row += count;
                }
            }
            nextRow += height;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        codec?.Dispose();
        codec = null;
    }
}
