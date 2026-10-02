using TeachingToolWpf.Core.Editing;
using TeachingToolWpf.Core.Imaging;
using TeachingToolWpf.Imaging;
using TeachingToolWpf.Imaging.Filters;

namespace TeachingToolWpf.Imaging.Tests;

public class RoiFilterTests
{
    [Fact] public async Task EachSelectedRoiUsesItsOwnRegisteredSettings()
    {
        var pixels = new PixelBuffer(16, 16, PixelKind.Gray8); pixels.Span.Fill(100);
        using var frame = new VideoFrame(1, 1, pixels);
        var service = new RoiFilterPreview(new ImageLoader("unused-for-live"), frame.Retain);
        var first = new Roi(Guid.NewGuid(), "first", new(new(8, 8), 8, 8, 0), null, false, 1) { Filter = new(RoiFilterKind.Binarize, 50) };
        var second = first with { Id = Guid.NewGuid(), Filter = new(RoiFilterKind.Binarize, 150) };
        var original = first with { Id = Guid.NewGuid(), Filter = new(RoiFilterKind.None) };
        var result = await service.ProcessAsync(new(new("live", "live", 16, 16), true, [first, second, original]), CancellationToken.None);
        Assert.Equal(2, result.Regions.Count);
        Assert.All(result.Regions[0].Gray8, value => Assert.Equal(255, value));
        Assert.All(result.Regions[1].Gray8, value => Assert.Equal(0, value));
    }
    [Fact] public async Task RotatedLiveRoiSamplesCorrectPixelsWithoutTakingFrameOwnership()
    {
        var pixels = new PixelBuffer(16, 16, PixelKind.Gray8);
        for (var y = 0; y < 16; y++) for (var x = 0; x < 16; x++) pixels.Bytes[y * 16 + x] = (byte)(x < 8 ? 0 : 255);
        using var frame = new VideoFrame(1, 1, pixels);
        var service = new RoiFilterPreview(new ImageLoader("unused-for-live"), frame.Retain);
        var roi = new Roi(Guid.NewGuid(), "90 degrees", new(new(8, 8), 8, 8, 90), null, false, 1);
        var request = new FilterRequest(new("live:Gray8", "Live", 16, 16), true, [roi with { Filter = new(RoiFilterKind.Binarize) }]);
        var region = Assert.Single((await service.ProcessAsync(request, CancellationToken.None)).Regions);
        Assert.All(region.Gray8.Take(8), pixel => Assert.Equal(255, pixel));
        Assert.All(region.Gray8.TakeLast(8), pixel => Assert.Equal(0, pixel));
        Assert.Equal(256, frame.Pixels.Length);
    }
    [Theory] [InlineData(PixelKind.Gray8)] [InlineData(PixelKind.Gray16)] [InlineData(PixelKind.Rgba32)] [InlineData(PixelKind.Bgra32)]
    public void BinarizeUsesExpectedIntensityAndLeavesInputUnchanged(PixelKind kind)
    {
        using var pixels = new PixelBuffer(32, 24, kind);
        var bpp = pixels.Stride / pixels.Width;
        for (var y = 0; y < 24; y++) for (var x = 0; x < 32; x++)
        { var offset = y * pixels.Stride + x * bpp; for (var b = 0; b < bpp; b++) pixels.Bytes[offset + b] = (byte)(x < 16 ? 40 : 210); }
        var before = pixels.Span.ToArray();
        var binary = OpenCvRoiFilter.Apply(pixels, new(RoiFilterKind.Binarize));
        Assert.Equal((byte)0, binary[0]); Assert.Equal((byte)255, binary[31]); Assert.Equal(before, pixels.Span.ToArray());
        var edge = OpenCvRoiFilter.Apply(pixels, new(RoiFilterKind.Edge));
        Assert.Contains((byte)255, edge); Assert.Equal((byte)0, edge[0]);
    }
    [Fact] public async Task LargeRotatedRoiUsesBoundedPreviewAndCancellation()
    {
        var service = new RoiFilterPreview(new ImageLoader("unused-for-procedural"), () => null);
        var roi = new Roi(Guid.NewGuid(), "회전", new(new(12000, 16000), 12000, 18000, 27), null, false, 1);
        var request = new FilterRequest(new("sample:24576:32768", "24K", 24576, 32768), false, [roi with { Filter = new(RoiFilterKind.Binarize) }]);
        var result = await service.ProcessAsync(request, CancellationToken.None);
        var region = Assert.Single(result.Regions);
        Assert.Equal(roi.Geometry, region.Geometry); Assert.InRange(region.Width, 1, 1024); Assert.InRange(region.Height, 1, 1024);
        Assert.Equal(region.Width * region.Height, region.Gray8.Length);
        Assert.All(region.Gray8, value => Assert.True(value is 0 or 255));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ProcessAsync(request, new CancellationToken(true)));
    }
}
