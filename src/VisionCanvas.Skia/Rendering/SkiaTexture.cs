using System.Runtime.InteropServices;
using SkiaSharp;
using VisionCanvas.Rendering.Abstractions;
using VisionImaging.Abstractions;

namespace VisionCanvas.Skia.Rendering;

/// <summary>타일·표시용 프레임만 소유한다. Gray16 원본은 보존하고 표시 복사본에서 8비트로 정규화한다.</summary>
internal sealed class SkiaTexture : IRenderTexture
{
    private const int HalfUshortDivisorRounding = 128;
    private const int UshortToByteDivisor = 257;

    public SkiaTexture(int width, int height, PixelKind kind)
    {
        Kind = kind;
        var color = kind switch
        {
            PixelKind.Gray8 or PixelKind.Gray16 => SKColorType.Gray8,
            PixelKind.Bgra32 => SKColorType.Bgra8888,
            PixelKind.Rgba32 => SKColorType.Rgba8888,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        Bitmap = new(new SKImageInfo(width, height, color,
            kind is PixelKind.Gray8 or PixelKind.Gray16 ? SKAlphaType.Opaque : SKAlphaType.Unpremul));
    }

    public SKBitmap Bitmap { get; }

    public PixelKind Kind { get; }

    public int Width => Bitmap.Width;

    public int Height => Bitmap.Height;

    public bool IsOpaque { get; private set; }

    public bool HasPixels { get; private set; }

    public long Bytes => Bitmap.ByteCount;

    public long AllocatedBytes => Bytes;

    public void Update(PixelBuffer pixels)
    {
        if (pixels.Width != Width || pixels.Height != Height || pixels.Kind != Kind)
        {
            throw new ArgumentException("표시 타일의 크기와 픽셀 형식이 일치해야 합니다.", nameof(pixels));
        }

        var target = Bitmap.GetPixelSpan();
        IsOpaque = true;
        for (var row = 0; row < Height; row++)
        {
            var source = pixels.Span.Slice(row * pixels.Stride);
            var destination = target.Slice(row * Bitmap.RowBytes);
            if (Kind == PixelKind.Gray16)
            {
                var values = MemoryMarshal.Cast<byte, ushort>(source[..(Width * 2)]);
                for (var column = 0; column < Width; column++)
                {
                    destination[column] = (byte)((values[column] + HalfUshortDivisorRounding) / UshortToByteDivisor);
                }
            }
            else
            {
                var bytes = Width * (Kind == PixelKind.Gray8 ? 1 : 4);
                source[..bytes].CopyTo(destination);
                if (Kind is PixelKind.Bgra32 or PixelKind.Rgba32)
                {
                    for (var column = 3; column < bytes; column += 4)
                    {
                        IsOpaque &= source[column] == byte.MaxValue;
                    }
                }
            }
        }

        Bitmap.NotifyPixelsChanged();
        HasPixels = true;
    }

    public void Dispose() => Bitmap.Dispose();
}
