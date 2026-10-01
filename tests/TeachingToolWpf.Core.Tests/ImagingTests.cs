using TeachingToolWpf.Core.Geometry;
using TeachingToolWpf.Core.Imaging;

namespace TeachingToolWpf.Core.Tests;

public class ImagingTests
{
    [Fact] public void HugeImageFitRequestsBoundedLodTiles()
    {
        var image = new ImageInfo("huge", "24K", 24576, 131072, PixelKind.Gray16, TilePlanner.LevelCount(24576, 131072));
        var view = ViewportTransform.Fit(image.Width, image.Height, 1920, 1080);
        var tiles = TilePlanner.Visible(image, view, 1920, 1080, 1);
        Assert.InRange(tiles.Count, 1, 20);
        Assert.All(tiles, t => Assert.True(t.Level > 0));
    }
    [Fact] public void OffImageViewHasNoTiles()
    {
        var image = new ImageInfo("test", "test", 100, 100, PixelKind.Gray8, 1);
        Assert.Empty(TilePlanner.Visible(image, new ViewportTransform(1, new PointD(2000, 2000)), 1000, 1000, 1));
    }
    [Fact] public void PixelLeaseHasCheckedSizeAndSingleReturn()
    {
        var pixels = new PixelBuffer(512, 64, PixelKind.Gray16);
        Assert.Equal(65536, pixels.Length);
        pixels.Dispose(); pixels.Dispose();
        Assert.Throws<ObjectDisposedException>(() => pixels.Bytes);
        Assert.Throws<OverflowException>(() => new PixelBuffer(int.MaxValue, 2, PixelKind.Rgba32));
    }
}
