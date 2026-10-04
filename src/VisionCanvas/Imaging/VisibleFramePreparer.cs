using TeachingToolWpf.Core.Geometry;
using TeachingToolWpf.Core.Imaging;
using TeachingToolWpf.Rendering;

namespace TeachingToolWpf.Imaging;

/// <summary>Preserves the full source lease; derives only the visible display pixels for PCIe upload.</summary>
public sealed class VisibleFramePreparer : IFramePreparer
{
    public PreparedFrame? Prepare(PixelBuffer source, ViewportTransform view, double width, double height, double dpi)
    {
        var a = view.ScreenToImage(new(0, 0));
        var b = view.ScreenToImage(new(width, height));
        var left = (int)Math.Clamp(Math.Floor(a.X) - 2, 0, source.Width);
        var top = (int)Math.Clamp(Math.Floor(a.Y) - 2, 0, source.Height);
        var right = (int)Math.Clamp(Math.Ceiling(b.X) + 2, 0, source.Width);
        var bottom = (int)Math.Clamp(Math.Ceiling(b.Y) + 2, 0, source.Height);
        if (right <= left || bottom <= top)
        {
            return null;
        }

        var level = Math.Clamp((int)Math.Floor(Math.Log2(1 / (view.Scale * dpi))), 0, 4);
        var factor = 1 << level;
        var pixels = new PixelBuffer((right - left + factor - 1) / factor, (bottom - top + factor - 1) / factor, source.Kind);
        try
        {
            BoxResampler.Resample(source, pixels, left, top, right - left, bottom - top, factor);
            return new(pixels, new(left, top, right, bottom));
        }
        catch
        {
            pixels.Dispose();
            throw;
        }
    }
}
