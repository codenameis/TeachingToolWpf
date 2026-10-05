using VisionImaging.Abstractions;

namespace TeachingToolWpf.Core.Tests;

public class ImagingTests
{
    [Fact]
    public void PixelLeaseHasCheckedSizeAndSingleReturn()
    {
        var pixels = new PixelBuffer(512, 64, PixelKind.Gray16);
        Assert.Equal(65536, pixels.Length);
        pixels.Dispose();
        pixels.Dispose();
        Assert.Throws<ObjectDisposedException>(() => pixels.Bytes);
        Assert.Throws<OverflowException>(() => new PixelBuffer(int.MaxValue, 2, PixelKind.Rgba32));
    }
}
