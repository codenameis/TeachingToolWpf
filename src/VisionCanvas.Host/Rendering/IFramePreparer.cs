using VisionGeometry;
using VisionImaging.Abstractions;

namespace VisionCanvas.Rendering;

public interface IFramePreparer
{
    PreparedFrame? Prepare(PixelBuffer source, ViewportTransform view, double width, double height, double dpi);
}
