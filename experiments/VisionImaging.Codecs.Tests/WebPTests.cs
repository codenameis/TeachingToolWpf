using System.Buffers.Binary;
using OpenCvSharp;
using VisionImaging.Abstractions;
using VisionImaging.Codecs;
using VisionImaging.Fixtures;

namespace VisionImaging.Codecs.Tests;

public sealed class WebPTests
{
    [Fact]
    public void LosslessRgbaPreservesStraightAlphaAndSequentialRows()
    {
        var (path, expected) = Fixture();
        using var reader = new NativeRasterDecoder().Open(path, default);
        Assert.Equal(PixelKind.Rgba32, reader.Kind);
        Assert.Equal((32, 17), (reader.Width, reader.Height));
        var actual = new byte[expected.Length];
        reader.ReadRows(0, 3, actual.AsSpan(0, 32 * 3 * 4), default);
        reader.ReadRows(3, 14, actual.AsSpan(32 * 3 * 4), default);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void InvalidRequestAndCancellationDoNotConsumeRows()
    {
        var (path, expected) = Fixture();
        using var reader = new NativeRasterDecoder().Open(path, default);
        Assert.Throws<ArgumentOutOfRangeException>(() => reader.ReadRows(1, 1, new byte[128], default));
        Assert.Throws<ArgumentException>(() => reader.ReadRows(0, 1, new byte[127], default));
        Assert.ThrowsAny<OperationCanceledException>(() => reader.ReadRows(0, 1, new byte[128], new(true)));
        var row = new byte[128];
        reader.ReadRows(0, 1, row, default);
        Assert.Equal(expected[..128], row);
    }

    [Fact]
    public void DisposeReleasesFileAndRejectsFurtherReads()
    {
        var (path, _) = Fixture();
        var reader = new NativeRasterDecoder().Open(path, default);
        reader.Dispose();
        reader.Dispose();
        Assert.Throws<ObjectDisposedException>(() => reader.ReadRows(0, 1, new byte[128], default));
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public void BudgetIsCheckedBeforeFullRgbaAllocation()
    {
        var (path, _) = Fixture();
        Assert.Throws<InvalidOperationException>(() => new NativeRasterDecoder(1024).Open(path, default));
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public void PreCancelledOpenDoesNotAcquireFile()
    {
        var (path, _) = Fixture();
        Assert.ThrowsAny<OperationCanceledException>(() => new NativeRasterDecoder().Open(path, new(true)));
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TruncatedInputIsRejectedAndReleased(bool correctContainerLength)
    {
        var (path, _) = Fixture();
        var bytes = File.ReadAllBytes(path);
        Array.Resize(ref bytes, bytes.Length - 5);
        if (correctContainerLength) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)(bytes.Length - 8));
        File.WriteAllBytes(path, bytes);
        Assert.Throws<InvalidDataException>(() => new NativeRasterDecoder().Open(path, default));
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public void OversizedRiffDeclarationIsRejectedByBudget()
    {
        var (path, _) = Fixture();
        var bytes = File.ReadAllBytes(path);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), uint.MaxValue);
        File.WriteAllBytes(path, bytes);
        Assert.Throws<InvalidOperationException>(() => new NativeRasterDecoder().Open(path, default));
    }

    [Fact]
    public void AnimationFlagIsExplicitlyRejected()
    {
        var (path, _) = Fixture();
        var original = File.ReadAllBytes(path);
        var bytes = new byte[original.Length + 18];
        original.AsSpan(0, 12).CopyTo(bytes);
        "VP8X"u8.CopyTo(bytes.AsSpan(12));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 10);
        bytes[20] = 0x12; // Animation and alpha; width/height minus one use 24-bit little endian.
        bytes[24] = 31;
        bytes[27] = 16;
        original.AsSpan(12).CopyTo(bytes.AsSpan(30));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)(bytes.Length - 8));
        File.WriteAllBytes(path, bytes);
        Assert.Throws<NotSupportedException>(() => new NativeRasterDecoder().Open(path, default));
    }

    [Fact]
    public void FileSignatureDeterminesDecoder()
    {
        var (path, expected) = Fixture();
        var renamed = path + ".bin";
        File.Copy(path, renamed);
        using var reader = new NativeRasterDecoder().Open(renamed, default);
        var actual = new byte[expected.Length];
        reader.ReadRows(0, 17, actual, default);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(90)]
    [InlineData(101)]
    public void RgbLossyAndLosslessHaveOpaqueAlpha(int quality)
    {
        var path = NewPath();
        var pixels = Enumerable.Range(0, 32 * 17).SelectMany(_ => new byte[] { 210, 90, 30 }).ToArray();
        ImageFixtureWriter.Write(path, 32, 17, 3, 8, pixels, (int)ImwriteFlags.WebPQuality, quality);
        using var reader = new NativeRasterDecoder().Open(path, default);
        var actual = new byte[32 * 17 * 4];
        reader.ReadRows(0, 17, actual, default);
        for (var i = 0; i < actual.Length; i += 4)
        {
            Assert.InRange(actual[i], 205, 215);
            Assert.InRange(actual[i + 1], 85, 95);
            Assert.InRange(actual[i + 2], 25, 35);
            Assert.Equal(255, actual[i + 3]);
        }
    }

    private static (string Path, byte[] Pixels) Fixture()
    {
        var path = NewPath();
        var pixels = new byte[32 * 17 * 4];
        new Random(781).NextBytes(pixels);
        for (var i = 0; i < pixels.Length; i += 4)
            if (pixels[i + 3] == 0) pixels[i] = pixels[i + 1] = pixels[i + 2] = 0;
        ImageFixtureWriter.Write(path, 32, 17, 4, 8, pixels, (int)ImwriteFlags.WebPQuality, 101);
        return (path, pixels);
    }

    private static string NewPath()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "원본.webp");
    }
}
