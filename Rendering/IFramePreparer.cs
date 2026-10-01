using TeachingToolWpf.Core.Geometry;
using TeachingToolWpf.Core.Imaging;

namespace TeachingToolWpf.Rendering;

public sealed record PreparedFrame(PixelBuffer Pixels, BoundsD ImageBounds) : IDisposable
{
    public void Dispose() => Pixels.Dispose();
}
public interface IFramePreparer
{
    PreparedFrame? Prepare(PixelBuffer source, ViewportTransform view, double width, double height, double dpi);
}
