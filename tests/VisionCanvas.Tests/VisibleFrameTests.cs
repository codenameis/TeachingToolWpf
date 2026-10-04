using System.Runtime.InteropServices;
using TeachingToolWpf.Core.Geometry;
using TeachingToolWpf.Core.Imaging;
using TeachingToolWpf.Imaging;

namespace TeachingToolWpf.Imaging.Tests;

public class VisibleFrameTests
{
    [Theory]
    [InlineData(PixelKind.Gray8)]
    [InlineData(PixelKind.Gray16)]
    [InlineData(PixelKind.Rgba32)]
    [InlineData(PixelKind.Bgra32)]
    public void VectorizedAndScalarAreaAveragesMatchAtOddEdges(PixelKind kind)
    {
        using var source = new PixelBuffer(201, 73, kind);
        new Random(42).NextBytes(source.Span);
        foreach (var factor in new[] { 1, 2, 4, 8, 16 })
        {
            using var scalar = new PixelBuffer((193 + factor - 1) / factor, (67 + factor - 1) / factor, kind);
            using var vector = new PixelBuffer(scalar.Width, scalar.Height, kind);
            BoxResampler.Resample(source, scalar, 3, 2, 193, 67, factor, false);
            BoxResampler.Resample(source, vector, 3, 2, 193, 67, factor);
            Assert.True(scalar.Span.SequenceEqual(vector.Span), $"{kind}, factor {factor}");
        }
    }

    [Fact]
    public void ZoomedGray16CropRetainsOriginalValuesAndImageCoordinates()
    {
        using var source = new PixelBuffer(100, 80, PixelKind.Gray16);
        var values = MemoryMarshal.Cast<byte, ushort>(source.Span);
        for (var i = 0;
i < values.Length;
i++)
        {
            values[i] = (ushort)i;
        }

        using var display = new VisibleFramePreparer().Prepare(source, new(2, new(-40, -20)), 100, 80, 1)!;
        Assert.Equal(new BoundsD(18, 8, 72, 52), display.ImageBounds);
        Assert.Equal(818, BitConverter.ToUInt16(display.Pixels.Bytes));
        Assert.Equal(100, source.Width);
        Assert.Equal(8000, MemoryMarshal.Cast<byte, ushort>(source.Span).Length);
    }

    [Fact]
    public void FitUsesAreaAverageWithoutChangingTheSource()
    {
        using var source = new PixelBuffer(8, 8, PixelKind.Gray8);
        for (var y = 0;
y < 8;
y++)
        {
            for (var x = 0;
x < 8;
x++)
            {
                source.Bytes[(y * 8) + x] = (byte)(x * 20);
            }
        }

        using var display = new VisibleFramePreparer().Prepare(source, new(.25, new()), 2, 2, 1)!;
        Assert.Equal(2, display.Pixels.Width);
        Assert.Equal(2, display.Pixels.Height);
        Assert.InRange(display.Pixels.Bytes[0], 29, 31);
        Assert.InRange(display.Pixels.Bytes[1], 109, 111);
        Assert.Equal(140, source.Bytes[7]);
    }
}
