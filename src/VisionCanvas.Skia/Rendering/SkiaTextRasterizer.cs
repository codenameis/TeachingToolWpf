using System.Runtime.InteropServices;
using SkiaSharp;
using VisionCanvas.Rendering;
using VisionImaging.Abstractions;

namespace VisionCanvas.Skia.Rendering;

internal sealed class SkiaTextRasterizer : ITextRasterizer, IDisposable
{
    private readonly SKTypeface _typeface = SKTypeface.FromFamilyName("Malgun Gothic");

    public PixelBuffer Rasterize(string text, double dpi)
    {
        using var font = new SKFont(_typeface, (float)(12 * dpi));
        using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        var width = Math.Max(1, (int)Math.Ceiling(Math.Min(font.MeasureText(text), 420 * dpi) + (2 * dpi)));
        var metrics = font.Metrics;
        var height = Math.Max(1, (int)Math.Ceiling(metrics.Descent - metrics.Ascent + (2 * dpi)));
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.DrawText(text, (float)dpi, (float)dpi - metrics.Ascent, SKTextAlign.Left, font, paint);
        }
        var pixels = new PixelBuffer(width, height, PixelKind.Bgra32);
        Marshal.Copy(bitmap.GetPixels(), pixels.Bytes, 0, pixels.Length);
        // 공통 텍스처 계약은 straight alpha다. 흰 글자의 RGB는 투명도와 독립적이다.
        for (var index = 0; index < pixels.Length; index += 4)
        {
            pixels.Bytes[index] = 255;
            pixels.Bytes[index + 1] = 255;
            pixels.Bytes[index + 2] = 255;
        }
        return pixels;
    }

    public void Dispose() => _typeface.Dispose();
}
