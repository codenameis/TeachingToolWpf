using System.Runtime.InteropServices;
using VisionCanvas.Core.Editing;
using VisionCanvas.Rendering;

namespace VisionCanvas.Tests;

public class RepeatedGridRenderingTests
{
    [Fact]
    public void FragmentedSummaryMergesSubpixelStepsAndDetailPreservesExactBlocks()
    {
        var grid = new RepeatedRoiGrid(10001, 10001, .25, new(4096, 4096)).Transform(new(default, 37, default));
        var whole = new RepeatedRoiPart(grid, RoiCellSet.All(grid.Count));
        var partial = new RepeatedRoiPart(grid, whole.Contained(new(-10000, -10000, grid.Center.X, 10000)));
        var stride = RepeatedRoiPipeline.SummaryStride(grid, .1);
        var drawing = RepeatedRoiPipeline.DrawingBlocks(partial, stride);
        Assert.True(partial.Blocks.Length > 5000);
        Assert.True(drawing.Length / 4 < 100);
        foreach (var block in partial.Blocks)
        {
            foreach (var point in new[] { (block.Left, block.Top), (block.Right, block.Bottom) })
            {
                Assert.Contains(Enumerable.Range(0, drawing.Length / 4), i => drawing[i * 4] <= point.Item1 && drawing[(i * 4) + 2] > point.Item1
                    && drawing[(i * 4) + 1] <= point.Item2 && drawing[(i * 4) + 3] > point.Item2);
            }
        }
        Assert.Equal(partial.Blocks.Length * 4, RepeatedRoiPipeline.DrawingBlocks(partial, 1).Length);
        var tiny = new RepeatedRoiPart(grid, RoiCellSet.Create([new(20002, 20004)]));
        Assert.Equal(RepeatedRoiPipeline.DrawingBlocks(tiny, 1), RepeatedRoiPipeline.DrawingBlocks(tiny, stride));
    }

    [Fact]
    public void DrawConstantsMatchTheShadersSixteenDwordRootLayout()
    {
        Assert.Equal(64, Marshal.SizeOf<RepeatedRoiPipeline.DrawConstants>());
        Assert.Equal(24, Marshal.OffsetOf<RepeatedRoiPipeline.DrawConstants>(nameof(RepeatedRoiPipeline.DrawConstants.Columns)).ToInt32());
        Assert.Equal(40, Marshal.OffsetOf<RepeatedRoiPipeline.DrawConstants>(nameof(RepeatedRoiPipeline.DrawConstants.SummaryStride)).ToInt32());
        Assert.Equal(48, Marshal.OffsetOf<RepeatedRoiPipeline.DrawConstants>(nameof(RepeatedRoiPipeline.DrawConstants.Color)).ToInt32());
    }

    [Theory]
    [InlineData(1024)]
    [InlineData(8192)]
    [InlineData(24576)]
    public void DetailZoomShowsIndividualCellsAtEverySupportedImageSize(int size)
    {
        var grid = new RepeatedRoiGrid(10001, 10001, size * .45 / 10001, new(size / 2d, size / 2d));
        var detailScale = Math.Clamp(12 / grid.Pitch, 1, 128);
        Assert.Equal(1u, RepeatedRoiPipeline.SummaryStride(grid, detailScale));
        Assert.True(RepeatedRoiPipeline.SummaryStride(grid, 600d / size) > 1);
    }
}
