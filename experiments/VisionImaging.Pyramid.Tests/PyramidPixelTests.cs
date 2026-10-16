using System.Buffers.Binary;
using VisionImaging.Abstractions;

namespace VisionImaging.Pyramid.Tests;

public sealed class PyramidPixelTests
{
    public static IEnumerable<object[]> Sizes()
    {
        foreach (var kind in new[] { PixelKind.Gray8, PixelKind.Gray16, PixelKind.Rgba32 })
        {
            foreach (var width in new[] { 1, 2, 3, 7, 8, 9, 10, 11, 12, 15, 16, 17, 31, 32, 33 })
            {
                yield return [kind, width, 513];
                yield return [kind, width, 514];
            }
            yield return [kind, 1024, 129];
            yield return [kind, 1031, 130];
        }
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void EveryPixelMatchesSeparableConvolutionIncludingEdgesAndAlpha(PixelKind kind, int width, int height)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var input = Path.Combine(root, "pixels.fixture");
        File.WriteAllText(input, "pixel-equivalence");
        var bytesPerPixel = kind == PixelKind.Gray16 ? 2 : kind == PixelKind.Gray8 ? 1 : 4;
        var expected = new byte[width * height * bytesPerPixel];
        // RGB뿐 아니라 alpha와 Gray16의 상위 비트까지 전체 범위에서 확인한다.
        new Random(462).NextBytes(expected);
        using var source = RawPyramidSource.Open(input, Path.Combine(root, "cache"),
            new PixelsDecoder(width, height, kind, expected), new Progress<string>(), default);
        var entry = Path.GetDirectoryName(Directory.EnumerateFiles(root, "image.json", SearchOption.AllDirectories).Single())!;
        for (var level = 0; level < source.Info.Levels; level++)
        {
            Assert.Equal(expected, File.ReadAllBytes(Path.Combine(entry, $"{level}.raw")));
            if (level + 1 < source.Info.Levels)
            {
                expected = ReferenceReduction(expected, width, height, kind);
                width = (width + 1) / 2;
                height = (height + 1) / 2;
            }
        }
    }

    // 제품 SIMD 코드와 독립적인 실수 가중치 합성곱이다. 각 축에서 반올림하는 기존 계약을 검증한다.
    private static byte[] ReferenceReduction(byte[] input, int width, int height, PixelKind kind)
    {
        var channels = kind == PixelKind.Rgba32 ? 4 : 1;
        var values = kind == PixelKind.Gray16
            ? Enumerable.Range(0, input.Length / 2).Select(i => (int)BinaryPrimitives.ReadUInt16LittleEndian(input.AsSpan(2 * i, 2))).ToArray()
            : input.Select(value => (int)value).ToArray();
        for (var axis = 0; axis < 2; axis++)
        {
            var length = axis == 0 ? height : width;
            double[] weights = length % 2 == 0 ? [0.125, 0.375, 0.375, 0.125] : [0.25, 0.5, 0.25];
            var nextWidth = axis == 0 ? width : (width + 1) / 2;
            var nextHeight = axis == 0 ? (height + 1) / 2 : height;
            var next = new int[nextWidth * nextHeight * channels];
            for (var y = 0; y < nextHeight; y++)
            {
                for (var x = 0; x < nextWidth; x++)
                {
                    for (var channel = 0; channel < channels; channel++)
                    {
                        double sum = 0;
                        for (var tap = 0; tap < weights.Length; tap++)
                        {
                            var sourceX = axis == 0 ? x : Math.Clamp((2 * x) + tap - 1, 0, width - 1);
                            var sourceY = axis == 0 ? Math.Clamp((2 * y) + tap - 1, 0, height - 1) : y;
                            sum += weights[tap] * values[(((sourceY * width) + sourceX) * channels) + channel];
                        }
                        next[(((y * nextWidth) + x) * channels) + channel] = (int)Math.Floor(sum + 0.5);
                    }
                }
            }
            values = next;
            width = nextWidth;
            height = nextHeight;
        }
        var output = new byte[values.Length * (kind == PixelKind.Gray16 ? 2 : 1)];
        for (var i = 0; i < values.Length; i++)
        {
            if (kind == PixelKind.Gray16)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(i * 2, 2), (ushort)values[i]);
            }
            else
            {
                output[i] = (byte)values[i];
            }
        }
        return output;
    }

    private sealed class PixelsDecoder(int width, int height, PixelKind kind, byte[] pixels) : IRasterDecoder, IRasterReader
    {
        public string CacheIdentity => "pixel-equivalence-v1";
        public int Width => width;
        public int Height => height;
        public PixelKind Kind => kind;
        public IRasterReader Open(string path, CancellationToken token) => this;
        public void ReadRows(int top, int rows, Span<byte> destination, CancellationToken token)
        {
            var stride = pixels.Length / height;
            pixels.AsSpan(top * stride, rows * stride).CopyTo(destination);
        }
        public void Dispose()
        {
        }
    }
}
