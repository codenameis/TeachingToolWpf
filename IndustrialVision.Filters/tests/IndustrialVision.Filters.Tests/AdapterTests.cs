using System.Buffers.Binary;
using IndustrialVision.Filters.Core;
using IndustrialVision.Filters.Examples;

namespace IndustrialVision.Filters.Tests;

public sealed class AdapterTests
{
    [Fact]
    public void Pgm16IsBigEndianWithoutEightBitReduction()
    {
        Directory.CreateDirectory("TestResults/inputs");
        var path = Path.GetFullPath("TestResults/inputs/gray16.pgm");
        File.WriteAllBytes(path, [.. System.Text.Encoding.ASCII.GetBytes("P5\n# unit\n3 1\n65535\n"), 0x10, 0x00, 0x10, 0x01, 0xff, 0xff]);
        using var source = FileTileSource.OpenPgm(path);
        using var output = new HostImage(source.Descriptor);
        source.Read(new(0, 0, 3, 1), output, default);
        Assert.Equal(new float[] { 4096, 4097, 65535 }, AccuracyTests.Read(output.View));
    }

    [Fact]
    public void Raw14LeftAlignmentIsNormalizedInAdapter()
    {
        Directory.CreateDirectory("TestResults/inputs");
        var path = Path.GetFullPath("TestResults/inputs/gray14.raw");
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, 16382 << 2);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2), 16383 << 2);
        File.WriteAllBytes(path, bytes);
        var descriptor = new ImageDescriptor(2, 1, PixelFormat.Gray16, validBits: 14);
        using var source = new FileTileSource(path, descriptor, bigEndian: true, leftAligned: true);
        using var output = new HostImage(descriptor);
        source.Read(new(0, 0, 2, 1), output, default);
        Assert.Equal(new float[] { 16382, 16383 }, AccuracyTests.Read(output.View));
    }
}
