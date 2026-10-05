using VisionImaging.Abstractions;

namespace VisionCanvas.Rendering;

public interface ITextRasterizer
{
    PixelBuffer Rasterize(string text, double dpi);
}
