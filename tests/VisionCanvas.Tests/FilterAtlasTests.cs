using VisionCanvas.Imaging.Filters;
using VisionCanvas.Rendering;
using VisionGeometry;

namespace TeachingToolWpf.Imaging.Tests;

public class FilterAtlasTests
{
    [Fact]
    public void ThousandsOfPreviewsUseFewTexturesAndPreserveEveryPixelAndDrawIndex()
    {
        var regions = Enumerable.Range(0, 7000).Select(i => new FilteredRoi(Guid.NewGuid(), new(new(100, 100), 24, 24, 25),
            24, 24, Enumerable.Repeat((byte)(i % 251), 24 * 24).ToArray())).ToArray();
        var pages = FilterAtlas.Pack(regions);
        Assert.InRange(pages.Count, 1, 4);
        Assert.Equal(7000, pages.Sum(p => p.Regions.Count));
        var indexes = new HashSet<int>();
        foreach (var page in pages)
        {
            using var pixels = page.Pixels();
            Assert.InRange(page.Width, 1, 2048);
            Assert.InRange(page.Height, 1, 2048);
            foreach (var p in page.Regions)
            {
                Assert.True(indexes.Add(p.Index));
                Assert.Same(regions[p.Index], p.Region);
                var uv = page.Uv(p);
                Assert.Equal(p.X, uv.Left * page.Width, 10);
                Assert.Equal(p.Y, uv.Top * page.Height, 10);
                for (var y = 0; y < p.Region.Height; y++)
                {
                    Assert.True(p.Region.Gray8.AsSpan(y * p.Region.Width, p.Region.Width)
                        .SequenceEqual(pixels.Span.Slice(((p.Y + y) * pixels.Stride) + p.X, p.Region.Width)));
                }
            }
        }
    }

    [Fact]
    public void MixedAspectRatiosArePackedWithoutLosingOriginalOrder()
    {
        var regions = Enumerable.Range(0, 4096).Select(i => new FilteredRoi(Guid.NewGuid(), new(new(50, 50), 10, 10, 0),
            i % 2 == 0 ? 1024 : 1, i % 2 == 0 ? 1 : 1024, new byte[1024])).ToArray();
        Assert.InRange(FilterAtlas.Pack(regions).Count, 1, 16);
        Assert.Empty(FilterAtlas.Pack([]));
    }
}
