using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using VisionImaging.Abstractions;

namespace VisionCanvas.Imaging;

/// <summary>Integer area averaging. AVX2 and scalar paths have the same pixel contract.</summary>
internal static class BoxResampler
{
    private static readonly ParallelOptions s_workers = new() { MaxDegreeOfParallelism = Math.Max(1, Math.Min(4, Environment.ProcessorCount / 2)) };

    public static void Resample(PixelBuffer source, PixelBuffer target, int left, int top, int width, int height, int factor, bool vectorize = true)
    {
        if (factor is not (1 or 2 or 4 or 8 or 16) || left < 0 || top < 0 || width <= 0 || height <= 0 || left + width > source.Width || top + height > source.Height)
        {
            throw new ArgumentOutOfRangeException(nameof(factor));
        }

        if (target.Width != (width + factor - 1) / factor || target.Height != (height + factor - 1) / factor || source.Kind != target.Kind)
        {
            throw new ArgumentException("축소 대상 크기와 형식이 맞지 않습니다.");
        }

        var bytes = source.Kind == PixelKind.Gray8 ? 1 : source.Kind == PixelKind.Gray16 ? 2 : 4;
        Parallel.For(0, target.Height, s_workers, y =>
        {
            var sourceRow = ((top + (y * factor)) * source.Stride) + (left * bytes);
            var destination = y * target.Stride;
            var rows = Math.Min(factor, height - (y * factor));
            if (factor == 1)
            {
                source.Bytes.AsSpan(sourceRow, width * bytes).CopyTo(target.Bytes.AsSpan(destination, width * bytes));
                return;
            }
            var x = 0;
            if (vectorize && Avx2.IsSupported && rows == factor)
            {
                x = source.Kind switch
                {
                    PixelKind.Gray8 => Gray8(source.Bytes, sourceRow, source.Stride, target.Bytes, destination, width, factor),
                    PixelKind.Gray16 => Gray16(source.Bytes, sourceRow, source.Stride, target.Bytes, destination, width, factor),
                    _ => Color(source.Bytes, sourceRow, source.Stride, target.Bytes, destination, width, factor)
                };
            }
            for (;
x < target.Width;
x++)
            {
                Scalar(source, target, sourceRow, destination, x, rows, Math.Min(factor, width - (x * factor)), factor);
            }
        });
    }

    private static void Scalar(PixelBuffer source, PixelBuffer target, int row, int destination, int x, int rows, int columns, int factor)
    {
        var channels = source.Kind is PixelKind.Rgba32 or PixelKind.Bgra32 ? 4 : 1;
        var bytes = source.Kind == PixelKind.Gray16 ? 2 : channels;
        var samples = rows * columns;
        for (var channel = 0;
channel < channels;
channel++)
        {
            uint sum = 0;
            for (var y = 0;
y < rows;
y++)
            {
                for (var dx = 0;
dx < columns;
dx++)
                {
                    var offset = row + (y * source.Stride) + (((x * factor) + dx) * bytes) + channel;
                    sum += source.Kind == PixelKind.Gray16 ? Unsafe.ReadUnaligned<ushort>(ref source.Bytes[offset]) : source.Bytes[offset];
                }
            }

            var value = (sum + ((uint)samples / 2)) / (uint)samples;
            if (bytes == 2)
            {
                Unsafe.WriteUnaligned(ref target.Bytes[destination + (x * 2)], (ushort)value);
            }
            else
            {
                target.Bytes[destination + (x * bytes) + channel] = (byte)value;
            }
        }
    }

    private static Vector256<int> Reduce(Vector256<int> value, int factor)
    {
        if (factor >= 2)
        {
            value = Avx2.HorizontalAdd(value, value);
        }

        if (factor >= 4)
        {
            value = Avx2.HorizontalAdd(value, value);
        }

        return value;
    }

    private static int Lane(int output, int count) => count == 8 ? output : output < count / 2 ? output : output + 4 - (count / 2);

    private static int Gray8(byte[] input, int row, int stride, byte[] output, int destination, int width, int factor)
    {
        var x = 0;
        var block = 32 / factor;
        var onesByte = Vector256.Create((sbyte)1);
        var onesShort = Vector256.Create((short)1);
        for (;
(x * factor) + 32 <= width;
x += block)
        {
            if (factor == 2)
            {
                var sum = Vector256<short>.Zero;
                for (var y = 0;
y < factor;
y++)
                {
                    sum = Avx2.Add(sum, Avx2.MultiplyAddAdjacent(Unsafe.ReadUnaligned<Vector256<byte>>(ref input[row + (y * stride) + (x * factor)]), onesByte));
                }

                for (var i = 0;
i < 16;
i++)
                {
                    output[destination + x + i] = (byte)((sum.GetElement(i) + 2) >> 2);
                }
            }
            else
            {
                var sum = Vector256<int>.Zero;
                for (var y = 0;
y < factor;
y++)
                {
                    var pairs = Avx2.MultiplyAddAdjacent(Unsafe.ReadUnaligned<Vector256<byte>>(ref input[row + (y * stride) + (x * factor)]), onesByte);
                    sum = Avx2.Add(sum, Avx2.MultiplyAddAdjacent(pairs, onesShort));
                }
                sum = Reduce(sum, factor / 4);
                for (var i = 0;
i < block;
i++)
                {
                    output[destination + x + i] = (byte)((sum.GetElement(Lane(i, block)) + (factor * factor / 2)) / (factor * factor));
                }
            }
        }
        return x;
    }

    private static int Gray16(byte[] input, int row, int stride, byte[] output, int destination, int width, int factor)
    {
        var x = 0;
        var block = Math.Max(1, 8 / factor);
        for (;
(x * factor) + Math.Max(8, factor) <= width;
x += block)
        {
            var sum = Vector256<int>.Zero;
            for (var column = 0;
column < Math.Max(8, factor);
column += 8)
            {
                for (var y = 0;
y < factor;
y++)
                {
                    sum = Avx2.Add(sum, Avx2.ConvertToVector256Int32(Unsafe.ReadUnaligned<Vector128<ushort>>(ref input[row + (y * stride) + (((x * factor) + column) * 2)])));
                }
            }

            sum = Reduce(sum, factor);
            for (var i = 0;
i < block;
i++)
            {
                var total = factor >= 8 ? sum.GetElement(0) + sum.GetElement(4) : sum.GetElement(Lane(i, block));
                Unsafe.WriteUnaligned(ref output[destination + ((x + i) * 2)], (ushort)((total + (factor * factor / 2)) / (factor * factor)));
            }
        }
        return x;
    }

    private static int Color(byte[] input, int row, int stride, byte[] output, int destination, int width, int factor)
    {
        var x = 0;
        var block = Math.Max(1, 8 / factor);
        var mask = Vector256.Create(0x00ff00ffu);
        for (;
(x * factor) + Math.Max(8, factor) <= width;
x += block)
        {
            var rb = Vector256<uint>.Zero;
            var ga = Vector256<uint>.Zero;
            for (var column = 0;
column < Math.Max(8, factor);
column += 8)
            {
                for (var y = 0;
y < factor;
y++)
                {
                    var value = Unsafe.ReadUnaligned<Vector256<uint>>(ref input[row + (y * stride) + (((x * factor) + column) * 4)]);
                    rb = Avx2.Add(rb, Avx2.And(value, mask));
                    ga = Avx2.Add(ga, Avx2.And(Avx2.ShiftRightLogical(value, 8), mask));
                }
            }

            rb = Reduce(rb.AsInt32(), factor).AsUInt32();
            ga = Reduce(ga.AsInt32(), factor).AsUInt32();
            for (var i = 0;
i < block;
i++)
            {
                var sumRb = factor >= 8 ? rb.GetElement(0) + rb.GetElement(4) : rb.GetElement(Lane(i, block));
                var sumGa = factor >= 8 ? ga.GetElement(0) + ga.GetElement(4) : ga.GetElement(Lane(i, block));
                var shift = factor == 2 ? 2 : factor == 4 ? 4 : factor == 8 ? 6 : 8;
                var round = (uint)(factor * factor / 2);
                var packedRound = round | (round << 16);
                var value = (((sumRb + packedRound) >> shift) & 0x00ff00ffu) | ((((sumGa + packedRound) >> shift) & 0x00ff00ffu) << 8);
                Unsafe.WriteUnaligned(ref output[destination + ((x + i) * 4)], value);
            }
        }
        return x;
    }
}
