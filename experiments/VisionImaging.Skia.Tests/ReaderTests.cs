using SkiaSharp;

namespace VisionImaging.Skia.Tests;

public sealed class ReaderTests
{
    [Fact]
    public void SequentialRowsRejectInvalidBuffersAndPreserveUnpremultipliedAlpha()
    {
        var path = Create(SKEncodedImageFormat.Png);
        using var reader = new SkiaRasterDecoder().Open(path, default);
        Assert.Throws<ArgumentOutOfRangeException>(() => reader.ReadRows(1, 1, new byte[8], default));
        Assert.Throws<ArgumentException>(() => reader.ReadRows(0, 1, new byte[1], default));
        byte[] row = new byte[8];
        reader.ReadRows(0, 1, row, default);
        Assert.Equal(new byte[] { 20, 40, 60, 128, 70, 80, 90, 255 }, row);
        Assert.Throws<ArgumentOutOfRangeException>(() => reader.ReadRows(0, 1, row, default));
        reader.ReadRows(1, 1, row, default);
        reader.Dispose();
        reader.Dispose();
        Assert.Throws<ObjectDisposedException>(() => reader.ReadRows(2, 1, row, default));
    }

    [Fact]
    public void CancellationIsCheckedBeforeOpenAndRowCopy()
    {
        var path = Create(SKEncodedImageFormat.Png);
        var canceled = new CancellationToken(true);
        Assert.Throws<OperationCanceledException>(() => new SkiaRasterDecoder().Open(path, canceled));
        using var reader = new SkiaRasterDecoder().Open(path, default);
        Assert.Throws<OperationCanceledException>(() => reader.ReadRows(0, 1, new byte[8], canceled));
        reader.ReadRows(0, 1, new byte[8], default);
    }

    [Fact]
    public void TruncatedJpegIsNotAcceptedAsSuccessfulDecode()
    {
        var path = Create(SKEncodedImageFormat.Jpeg);
        var bytes = File.ReadAllBytes(path);
        File.WriteAllBytes(path, bytes[..(bytes.Length / 2)]);
        Assert.Throws<InvalidDataException>(() => new SkiaRasterDecoder().Open(path, default));
    }

    private static string Create(SKEncodedImageFormat format)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, Guid.NewGuid().ToString("N"));
        using var bitmap = new SKBitmap(new SKImageInfo(2, 2, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        System.Runtime.InteropServices.Marshal.Copy(new byte[] { 20, 40, 60, 128, 70, 80, 90, 255, 3, 5, 7, 0, 9, 11, 13, 255 }, 0, bitmap.GetPixels(), 16);
        using var encoded = bitmap.Encode(format, 95);
        using var file = File.Create(path);
        encoded.SaveTo(file);
        return path;
    }
}
