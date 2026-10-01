using TeachingToolWpf.Core.Imaging;

namespace TeachingToolWpf.Rendering;

public interface ITextRasterizer
{
    PixelBuffer Rasterize(string text, double dpi);
}
