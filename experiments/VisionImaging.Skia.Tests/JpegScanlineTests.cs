using System.Runtime.InteropServices;
using SkiaSharp;

namespace VisionImaging.Skia.Tests;

public sealed class JpegScanlineTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(17)]
    [InlineData(130)]
    public void BatchesMatchFullDecodeAndPreserveCallerBuffer(int batch)
    {
        var path = CreateJpeg();
        using var reference = SKCodec.Create(path);
        var info = new SKImageInfo(reference.Info.Width, reference.Info.Height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        var expected = new byte[info.BytesSize];
        Assert.Equal(SKCodecResult.Success, reference.GetPixels(info, expected));
        reference.Dispose();
        using var reader = new SkiaRasterDecoder().Open(path, default);
        Assert.Throws<ArgumentOutOfRangeException>(() => reader.ReadRows(1, 1, new byte[info.RowBytes], default));
        Assert.Throws<ArgumentException>(() => reader.ReadRows(0, 1, new byte[1], default));
        Assert.Throws<OperationCanceledException>(() => reader.ReadRows(0, 1, new byte[info.RowBytes], new CancellationToken(true)));
        for (var top = 0; top < reader.Height; top += batch)
        {
            var rows = Math.Min(batch, reader.Height - top);
            var length = rows * info.RowBytes;
            var buffer = Enumerable.Repeat((byte)213, length + 16).ToArray();
            reader.ReadRows(top, rows, buffer, default);
            Assert.Equal(expected.AsSpan(top * info.RowBytes, length).ToArray(), buffer[..length]);
            Assert.All(buffer[length..], value => Assert.Equal(213, value));
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => reader.ReadRows(reader.Height, 1, new byte[info.RowBytes], default));
        reader.Dispose();
        reader.Dispose();
        Assert.Throws<ObjectDisposedException>(() => reader.ReadRows(0, 1, new byte[info.RowBytes], default));
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public void MissingEntropyRowsFailInsteadOfPublishingPaddedPixels()
    {
        var path = CreateJpeg();
        var bytes = File.ReadAllBytes(path);
        File.WriteAllBytes(path, bytes[..(bytes.Length * 2 / 3)]);
        Assert.Throws<InvalidDataException>(() =>
        {
            using var reader = new SkiaRasterDecoder().Open(path, default);
            reader.ReadRows(0, reader.Height, new byte[reader.Width * reader.Height * 4], default);
        });
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    private static string CreateJpeg()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".jpg");
        using var bitmap = new SKBitmap(new SKImageInfo(193, 137, SKColorType.Rgba8888, SKAlphaType.Opaque));
        var bytes = new byte[bitmap.ByteCount];
        new Random(147).NextBytes(bytes);
        for (var offset = 3; offset < bytes.Length; offset += 4) bytes[offset] = 255;
        Marshal.Copy(bytes, 0, bitmap.GetPixels(), bytes.Length);
        using var data = bitmap.Encode(SKEncodedImageFormat.Jpeg, 95);
        using var output = File.Create(path);
        data.SaveTo(output);
        return path;
    }
}
