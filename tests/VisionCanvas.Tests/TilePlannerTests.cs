using VisionCanvas.Imaging;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace VisionCanvas.Tests;

public class TilePlannerTests
{
    [Fact]
    public void HugeImageFitRequestsBoundedLodTiles()
    {
        var image = new ImageInfo("huge", "24K", 24576, 131072, PixelKind.Gray16, ImageInfo.CalculateLevelCount(24576, 131072));
        var view = ViewportTransform.Fit(image.Width, image.Height, 1920, 1080);
        var tiles = TilePlanner.Visible(image, view, 1920, 1080, 1);
        Assert.InRange(tiles.Count, 1, 20);
        Assert.All(tiles, t => Assert.True(t.Level > 0));
    }

    [Fact]
    public void OffImageViewHasNoTiles()
    {
        var image = new ImageInfo("test", "test", 100, 100, PixelKind.Gray8, 1);
        Assert.Empty(TilePlanner.Visible(image, new ViewportTransform(1, new PointD(2000, 2000)), 1000, 1000, 1));
    }
}
