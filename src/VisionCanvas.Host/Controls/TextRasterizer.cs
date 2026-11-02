using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VisionCanvas.Rendering;
using VisionImaging.Abstractions;

namespace VisionCanvas.Controls;

/// <summary>Creates isolated WPF text drawings on the calling STA. No visual from the UI is shared.</summary>
public sealed partial class TextRasterizer : ITextBatchRasterizer
{
    public PixelBuffer Rasterize(string text, double dpi)
        => RasterizeFormatted(Format(text, dpi), dpi);

    private static PixelBuffer RasterizeFormatted(FormattedText formatted, double dpi)
    {
        var width = Math.Max(1, (int)Math.Ceiling((formatted.WidthIncludingTrailingWhitespace + 2) * dpi));
        var height = Math.Max(1, (int)Math.Ceiling((formatted.Height + 2) * dpi));
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            context.DrawText(formatted, new Point(1, 1));
        }
        return Render(drawing, width, height, dpi);
    }

    private static FormattedText Format(string text, double dpi)
        => new(text, CultureInfo.GetCultureInfo("ko-KR"), FlowDirection.LeftToRight,
            new Typeface("Segoe UI, Malgun Gothic"), 12, Brushes.White, dpi)
        {
            MaxTextWidth = 420,
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis
        };

    private static PixelBuffer Render(DrawingVisual drawing, int width, int height, double dpi)
    {
        var bitmap = new RenderTargetBitmap(width, height, dpi * 96, dpi * 96, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        var result = new PixelBuffer(width, height, PixelKind.Bgra32);
        bitmap.CopyPixels(result.Bytes, result.Stride, 0);
        // White glyphs use straight alpha in the shared sprite pipeline.
        for (var i = 0; i < result.Length; i += 4)
        {
            result.Bytes[i] = result.Bytes[i + 1] = result.Bytes[i + 2] = 255;
        }

        return result;
    }
}
