using VisionImaging;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Imaging.Tests;

public sealed class ProceduralTilePixelTests
{
    [Theory]
    [InlineData(24585, 8201)]
    [InlineData(2055, 2053)]
    public void VectorizedGeneratorKeepsEveryChannelAcrossLevelsTileSeamsAndPartialTiles(int imageWidth, int imageHeight)
    {
        using var source = new ProceduralTileSource(imageWidth, imageHeight);
        for (var level = 0; level < source.Info.Levels; level++)
        {
            var factor = 1 << level;
            var width = (source.Info.Width + factor - 1) / factor;
            var height = (source.Info.Height + factor - 1) / factor;
            var lastX = (width - 1) / 512;
            var lastY = (height - 1) / 512;
            int[] columns = [0, Math.Min(1, lastX), Math.Min(2, lastX), Math.Min(3, lastX), lastX];
            int[] rows = [0, Math.Min(1, lastY), Math.Min(2, lastY), Math.Min(3, lastY), lastY];
            // 대각선 타일뿐 아니라 X/Y 조합 전체에서 pad/pin 우선순위와 edge mask를 검증한다.
            var keys = columns.SelectMany(x => rows.Select(y => new TileKey(level, x, y)));
            foreach (var key in keys.Distinct())
            {
                using var actual = source.Read(key, default);
                var expected = new byte[actual.Length];
                for (var y = 0; y < actual.Height; y++)
                {
                    for (var x = 0; x < actual.Width; x++)
                    {
                        var wx = ((key.X * 512) + x) * factor;
                        var wy = ((key.Y * 512) + y) * factor;
                        var cellX = wx % 2048;
                        var cellY = wy % 2048;
                        var edge = Math.Min(cellX, 2048 - cellX) < 24 || Math.Min(cellY, 2048 - cellY) < 24;
                        var pad = cellX > 360 && cellX < 1680 && cellY > 640 && cellY < 1400;
                        var pin = cellX > 250 && cellX < 1800 && cellX % 180 < 80 &&
                            ((cellY > 490 && cellY < 640) || (cellY > 1400 && cellY < 1550));
                        var trace = Math.Abs(cellX - cellY) < 12 || Math.Abs(cellX + cellY - 2048) < 12;
                        var noise = ((wx * 13) ^ (wy * 7)) & 7;
                        var index = (y * actual.Stride) + (x * 4);
                        expected[index] = (byte)((pad ? 36 : pin ? 195 : edge ? 20 : trace ? 95 : 40) + noise);
                        expected[index + 1] = (byte)((pad ? 43 : pin ? 181 : edge ? 37 : trace ? 133 : 83) + noise);
                        expected[index + 2] = (byte)((pad ? 57 : pin ? 135 : edge ? 44 : trace ? 116 : 83) + noise);
                        expected[index + 3] = 255;
                    }
                }
                Assert.True(expected.AsSpan().SequenceEqual(actual.Span), $"Pixel mismatch at {key}.");
            }
        }
    }

    [Fact]
    public void CanceledGeneratorDoesNotReturnPixels()
    {
        using var source = new ProceduralTileSource();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => source.Read(new(0, 0, 0), cancellation.Token));
    }
}
