using TeachingToolWpf.Imaging.Matching;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Imaging.Tests;

public sealed class InspectionGrayConverterTests
{
    [Theory]
    [InlineData(PixelKind.Rgba32)]
    [InlineData(PixelKind.Bgra32)]
    public void EveryRgbCombinationPreservesIntegerWeightsWithoutSaturation(PixelKind kind)
    {
        const int Pixels = 256 * 256;
        var input = new byte[Pixels * 4];
        var expected = new byte[Pixels];
        var actual = new byte[Pixels];
        for (var third = 0; third < 256; third++)
        {
            for (var first = 0; first < 256; first++)
            {
                for (var green = 0; green < 256; green++)
                {
                    var pixel = (first * 256) + green;
                    input[pixel * 4] = (byte)first;
                    input[(pixel * 4) + 1] = (byte)green;
                    input[(pixel * 4) + 2] = (byte)third;
                    input[(pixel * 4) + 3] = (byte)(first ^ green ^ third);
                    var red = kind == PixelKind.Rgba32 ? first : third;
                    var blue = kind == PixelKind.Rgba32 ? third : first;
                    expected[pixel] = (byte)(((77 * red) + (150 * green) + (29 * blue) + 128) >> 8);
                }
            }
            InspectionGrayConverter.ConvertRow(input, actual, kind);
            Assert.Equal(expected, actual);
        }
    }

    [Theory]
    [InlineData(PixelKind.Gray8)]
    [InlineData(PixelKind.Gray16)]
    [InlineData(PixelKind.Rgba32)]
    [InlineData(PixelKind.Bgra32)]
    public void VectorAndScalarPreserveExactPolicyAcrossOffsetsTailsAndColors(PixelKind kind)
    {
        var random = new Random(1753);
        var bytesPerPixel = kind == PixelKind.Gray8 ? 1 : kind == PixelKind.Gray16 ? 2 : 4;
        for (var width = 1; width < 530; width++)
        {
            const int Offset = 3;
            var input = new byte[(width * bytesPerPixel) + Offset + 5];
            random.NextBytes(input);
            var scalar = new byte[width + 7];
            var vector = new byte[width + 7];
            scalar.AsSpan().Fill(231);
            vector.AsSpan().Fill(231);
            var source = input.AsSpan(Offset, width * bytesPerPixel);
            InspectionGrayConverter.ConvertRow(source, scalar.AsSpan(2, width), kind, false);
            InspectionGrayConverter.ConvertRow(source, vector.AsSpan(2, width), kind);
            Assert.Equal(scalar, vector);
            for (var x = 0; x < width; x++)
            {
                var index = x * bytesPerPixel;
                var expected = kind switch
                {
                    PixelKind.Gray8 => source[index],
                    PixelKind.Gray16 => source[index + 1],
                    PixelKind.Rgba32 => ((77 * source[index]) + (150 * source[index + 1]) + (29 * source[index + 2]) + 128) >> 8,
                    _ => ((29 * source[index]) + (150 * source[index + 1]) + (77 * source[index + 2]) + 128) >> 8
                };
                Assert.Equal(expected, vector[x + 2]);
            }
        }
    }
}
