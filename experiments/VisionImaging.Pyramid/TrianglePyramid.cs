using System.Buffers;
using System.IO;
using System.Runtime.InteropServices;
using VisionImaging.Abstractions;

namespace VisionImaging.Pyramid;

/// <summary>2배 축소의 삼각 필터. 출력 크기 올림에 맞춰 중심을 정렬하고 경계를 복제한다.</summary>
internal static class TrianglePyramid
{
    public static long Reduce(string input, string output, int width, int height, PixelKind kind, CancellationToken token)
    {
        var bytesPerPixel = kind == PixelKind.Gray16 ? 2 : kind == PixelKind.Gray8 ? 1 : 4;
        var inputStride = checked(width * bytesPerPixel);
        if (inputStride > 4 * 1024 * 1024)
        {
            throw new NotSupportedException("축소용 원본 한 행은4MiB 이하여야 합니다.");
        }
        using var destination = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024);
        var outputWidth = (width + 1) / 2;
        var outputHeight = (height + 1) / 2;
        var stride = checked(outputWidth * bytesPerPixel);
        var bandHeight = Math.Min(64, Math.Max(1, ((16 * 1024 * 1024 / inputStride) - 2) / 2));
        using var source = new RawRowBand(input, inputStride, height, Math.Min(height, 2 * bandHeight + 2));
        var band = ArrayPool<byte>.Shared.Rent(checked(stride * bandHeight));
        long scratchBytes = 0;
        long peakScratchBytes = 0;
        try
        {
            for (var top = 0; top < outputHeight; top += bandHeight)
            {
                var rows = Math.Min(bandHeight, outputHeight - top);
                var firstInputRow = Math.Max(0, 2 * top - 1);
                var lastInputRow = Math.Min(height - 1, 2 * (top + rows - 1) + 2);
                source.Load(firstInputRow, lastInputRow - firstInputRow + 1, token);
                Parallel.For(0, rows, new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = Math.Min(8, Environment.ProcessorCount) }, row =>
                {
                    var scratch = ArrayPool<byte>.Shared.Rent(source.Stride);
                    var activeBytes = Interlocked.Add(ref scratchBytes, scratch.Length);
                    var observedPeak = Volatile.Read(ref peakScratchBytes);
                    while (activeBytes > observedPeak)
                    {
                        var previous = Interlocked.CompareExchange(ref peakScratchBytes, activeBytes, observedPeak);
                        if (previous == observedPeak)
                        {
                            break;
                        }
                        observedPeak = previous;
                    }
                    try
                    {
                        ReduceRow(source, top + row, width, height, kind, scratch, band.AsSpan(row * stride, stride));
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(scratch);
                        Interlocked.Add(ref scratchBytes, -scratch.Length);
                    }
                });
                destination.Write(band, 0, rows * stride);
            }
            return source.BufferBytes + band.LongLength + peakScratchBytes;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(band);
        }
    }

    private static void ReduceRow(RawRowBand source, int y, int width, int height, PixelKind kind, Span<byte> scratch, Span<byte> output)
    {
        var a = source.Row(Math.Clamp((2 * y) - 1, 0, height - 1));
        var b = source.Row(Math.Min(2 * y, height - 1));
        var c = source.Row(Math.Min((2 * y) + 1, height - 1));
        var d = source.Row(Math.Min((2 * y) + 2, height - 1));
        if (kind == PixelKind.Gray16)
        {
            var aa = MemoryMarshal.Cast<byte, ushort>(a);
            var bb = MemoryMarshal.Cast<byte, ushort>(b);
            var cc = MemoryMarshal.Cast<byte, ushort>(c);
            var dd = MemoryMarshal.Cast<byte, ushort>(d);
            var vertical = MemoryMarshal.Cast<byte, ushort>(scratch)[..width];
            for (var x = 0; x < width; x++)
            {
                vertical[x] = (ushort)Filter(aa[x], bb[x], cc[x], dd[x], height % 2 != 0);
            }
            var target = MemoryMarshal.Cast<byte, ushort>(output);
            for (var x = 0; x < target.Length; x++)
            {
                target[x] = (ushort)Filter(vertical[Math.Max((2 * x) - 1, 0)], vertical[Math.Min(2 * x, width - 1)],
                    vertical[Math.Min((2 * x) + 1, width - 1)], vertical[Math.Min((2 * x) + 2, width - 1)], width % 2 != 0);
            }
            return;
        }
        var channels = kind == PixelKind.Gray8 ? 1 : 4;
        var length = width * channels;
        for (var x = 0; x < length; x++)
        {
            scratch[x] = (byte)Filter(a[x], b[x], c[x], d[x], height % 2 != 0);
        }
        for (var x = 0; x < output.Length / channels; x++)
        {
            for (var channel = 0; channel < channels; channel++)
            {
                output[(x * channels) + channel] = (byte)Filter(scratch[(Math.Max((2 * x) - 1, 0) * channels) + channel],
                    scratch[(Math.Min(2 * x, width - 1) * channels) + channel],
                    scratch[(Math.Min((2 * x) + 1, width - 1) * channels) + channel],
                    scratch[(Math.Min((2 * x) + 2, width - 1) * channels) + channel], width % 2 != 0);
            }
        }
    }

    // 짝수 길이는 반 화소 중심, 홀수 길이는 정수 화소 중심이며 축마다 반올림한다.
    private static int Filter(int a, int b, int c, int d, bool oddLength) => oddLength
        ? (a + (2 * b) + c + 2) / 4
        : (a + (3 * b) + (3 * c) + d + 4) / 8;
}
