using static VisionImaging.TestFixtures.PngFixture;
using System.Buffers.Binary;
using System.IO.Compression;

namespace VisionImaging.Skia.Tests;

public sealed class PngTests
{
    [Theory]
    [InlineData("palette")]
    [InlineData("low-gray")]
    [InlineData("gray-transparency")]
    public void PaletteLowBitGrayAndTransparencyExpandWithoutColorLoss(string layout)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "expanded.png");
        byte[] expected = layout switch
        {
            "palette" => [10, 20, 30, 0, 40, 50, 60, 85, 70, 80, 90, 170, 100, 110, 120, 255],
            "low-gray" => [0, 255, 0, 255],
            _ => [0, 0, 0, 255, 7, 7, 7, 0, 128, 128, 128, 255, 255, 255, 255, 255]
        };
        using (var file = File.Create(path))
        {
            file.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
            byte[] header = [0, 0, 0, 4, 0, 0, 0, 1, layout == "palette" ? (byte)2 : layout == "low-gray" ? (byte)1 : (byte)8,
                layout == "palette" ? (byte)3 : (byte)0, 0, 0, 0];
            Chunk(file, "IHDR"u8.ToArray(), header);
            if (layout == "palette")
            {
                Chunk(file, "PLTE"u8.ToArray(), [10, 20, 30, 40, 50, 60, 70, 80, 90, 100, 110, 120]);
                Chunk(file, "tRNS"u8.ToArray(), [0, 85, 170, 255]);
            }
            else if (layout == "gray-transparency")
            {
                Chunk(file, "tRNS"u8.ToArray(), [0, 7]);
            }
            using var compressed = new MemoryStream();
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, true))
            {
                zlib.Write(layout == "palette" ? [0, 0x1b] : layout == "low-gray" ? [0, 0x50] : [0, 0, 7, 128, 255]);
            }
            Chunk(file, "IDAT"u8.ToArray(), compressed.ToArray());
            Chunk(file, "IEND"u8.ToArray(), []);
        }
        using var reader = new SkiaRasterDecoder().Open(path, default);
        var pixels = new byte[expected.Length];
        reader.ReadRows(0, 1, pixels, default);
        Assert.Equal(expected, pixels);
    }

    [Theory]
    [InlineData(8, 1, false)]
    [InlineData(8, 4, false)]
    [InlineData(16, 1, false)]
    [InlineData(8, 1, true)]
    [InlineData(8, 4, true)]
    [InlineData(16, 1, true)]
    public void OriginalEightBitPixelsSurviveAndSixteenBitInputIsExplicitlyRejected(int bits, int channels, bool interlaced)
    {
        var (file, expected) = Create(bits, channels, interlaced);
        if (bits == 16) { Assert.Throws<NotSupportedException>(() => new SkiaRasterDecoder().Open(file, default)); return; }
        using var reader = new SkiaRasterDecoder().Open(file, CancellationToken.None);
        var actual = new byte[expected.Length];
        for (var top = 0; top < reader.Height; top++)
        {
            reader.ReadRows(top, 1, actual.AsSpan(top * reader.Width * channels * (bits / 8)), CancellationToken.None);
        }
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void CorruptedPngCrcIsAnError()
    {
        var (file, _) = Create(8, 4, false);
        var data = File.ReadAllBytes(file);
        data[^15] ^= 1;
        File.WriteAllBytes(file, data);
        Assert.Throws<InvalidDataException>(() =>
        {
            using var reader = new SkiaRasterDecoder().Open(file, CancellationToken.None);
            reader.ReadRows(0, reader.Height, new byte[reader.Width * reader.Height * 4], CancellationToken.None);
        });
        using var exclusive = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public void FullBitmapBudgetIsEnforcedBeforeAllocatingPixels()
    {
        var (file, _) = Create(8, 4, false);
        var original = File.ReadAllBytes(file);
        using (var output = File.Create(file))
        {
            output.Write(original.AsSpan(0, 8));
            var header = original.AsSpan(16, 13).ToArray();
            BinaryPrimitives.WriteInt32BigEndian(header, 32768);
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), 32768);
            Chunk(output, "IHDR"u8.ToArray(), header);
            output.Write(original.AsSpan(33));
        }
        Assert.Throws<NotSupportedException>(() => new SkiaRasterDecoder().Open(file, default));
    }

}
