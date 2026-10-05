using System.Collections.Immutable;
using VisionCanvas.Rendering;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace VisionCanvas.Controls;

public sealed partial class WpfTextRasterizer
{
    public TextRasterBatch? RasterizeBatch(IEnumerable<string> texts, double dpi)
    {
        var entries = texts.Distinct(StringComparer.Ordinal).Select(text => (Text: text, Formatted: Format(text, dpi))).ToArray();
        if (entries.Length == 0)
        {
            return null;
        }
        // 유효 래스터 32 MiB 이하, 단일 atlas만 보관한다. GPU 정렬/전체 메모리 상한은 아니다.
        var cellWidth = entries.Max(entry => Math.Max(1, (int)Math.Ceiling((entry.Formatted.WidthIncludingTrailingWhitespace + 2) * dpi))) + 2;
        var cellHeight = entries.Max(entry => Math.Max(1, (int)Math.Ceiling((entry.Formatted.Height + 2) * dpi))) + 2;
        var columns = Math.Max(1, 2048 / cellWidth);
        var rows = (entries.Length + columns - 1) / columns;
        var width = Math.Min(columns, entries.Length) * cellWidth;
        var height = rows * cellHeight;
        if (width > 16384 || height > 16384 || (long)width * height > 8 * 1024 * 1024)
        {
            return null;
        }
        var regions = ImmutableDictionary.CreateBuilder<string, BoundsD>(StringComparer.Ordinal);
        var pixels = new PixelBuffer(width, height, PixelKind.Bgra32);
        try
        {
            pixels.Span.Clear();
            for (var index = 0; index < entries.Length; index++)
            {
                var entry = entries[index];
                var x = (index % columns * cellWidth) + 1;
                var y = (index / columns * cellHeight) + 1;
                var textWidth = Math.Max(1, (int)Math.Ceiling((entry.Formatted.WidthIncludingTrailingWhitespace + 2) * dpi));
                var textHeight = Math.Max(1, (int)Math.Ceiling((entry.Formatted.Height + 2) * dpi));
                regions.Add(entry.Text, new(x, y, x + textWidth, y + textHeight));
                // WPF의 큰 drawing 안에서 이동한 glyph는 알파 반올림이 달라진다.
                // 기존 원점의 래스터를 그대로 복사하고 GPU 업로드/텍스처만 묶어 글자 픽셀을 보존한다.
                using var label = RasterizeFormatted(entry.Formatted, dpi);
                for (var row = 0; row < label.Height; row++)
                {
                    label.Span.Slice(row * label.Stride, label.Width * 4)
                        .CopyTo(pixels.Span.Slice(((y + row) * pixels.Stride) + (x * 4)));
                }
            }
            return new(pixels, regions.ToImmutable());
        }
        catch
        {
            pixels.Dispose();
            throw;
        }
    }
}
