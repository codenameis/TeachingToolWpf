using VisionCanvas.Controls;

namespace TeachingToolWpf.Imaging.Tests;

public sealed class TextBatchRasterizerTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void AtlasPreservesEveryLabelSizeAndAlpha(double dpi)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var rasterizer = new TextRasterizer();
                var texts = Enumerable.Range(1, 256).Select(index => $"#{index} 점수 {index / 256d:F3}").ToArray();
                using var batch = rasterizer.RasterizeBatch(texts, dpi);
                Assert.NotNull(batch);
                Assert.Equal(texts.Length, batch.Regions.Count);
                Assert.True(batch.Pixels.Length <= 32 * 1024 * 1024);
                foreach (var text in texts.Where((_, index) => index % 31 == 0))
                {
                    using var single = rasterizer.Rasterize(text, dpi);
                    var bounds = batch.Regions[text];
                    Assert.Equal(single.Width, bounds.Width);
                    Assert.Equal(single.Height, bounds.Height);
                    for (var y = 0; y < single.Height; y++)
                    {
                        for (var x = 0; x < single.Width; x++)
                        {
                            var expected = single.Bytes[(y * single.Stride) + (x * 4) + 3];
                            var actual = batch.Pixels.Bytes[(((int)bounds.Top + y) * batch.Pixels.Stride) + (((int)bounds.Left + x) * 4) + 3];
                            Assert.Equal(expected, actual);
                        }
                    }
                }
            }
            catch (Exception error)
            {
                failure = error;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
