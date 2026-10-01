using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TeachingToolWpf.Core.Imaging;
using TeachingToolWpf.Rendering;

namespace TeachingToolWpf.Controls;

/// <summary>Creates isolated text drawings on the calling STA. No visual from the UI is shared.</summary>
public sealed class WpfTextRasterizer : ITextRasterizer
{
    public PixelBuffer Rasterize(string text, double dpi)
    {
        var formatted = new FormattedText(text, CultureInfo.GetCultureInfo("ko-KR"), FlowDirection.LeftToRight,
            new Typeface("Segoe UI, Malgun Gothic"), 12, Brushes.White, dpi)
        { MaxTextWidth = 420, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
        var width = Math.Max(1, (int)Math.Ceiling((formatted.WidthIncludingTrailingWhitespace + 2) * dpi));
        var height = Math.Max(1, (int)Math.Ceiling((formatted.Height + 2) * dpi));
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen()) context.DrawText(formatted, new Point(1, 1));
        var bitmap = new RenderTargetBitmap(width, height, dpi * 96, dpi * 96, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        var result = new PixelBuffer(width, height, PixelKind.Bgra32);
        bitmap.CopyPixels(result.Bytes, result.Stride, 0);
        // White glyphs use straight alpha in the shared sprite pipeline.
        for (var i = 0; i < result.Length; i += 4) result.Bytes[i] = result.Bytes[i + 1] = result.Bytes[i + 2] = 255;
        return result;
    }
}
