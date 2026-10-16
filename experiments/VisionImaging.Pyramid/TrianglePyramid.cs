using System.Buffers;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
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
        using var source = new RawRowBand(input, inputStride, height, Math.Min(height, (2 * bandHeight) + 2));
        var band = ArrayPool<byte>.Shared.Rent(checked(stride * bandHeight));
        long scratchBytes = 0;
        long peakScratchBytes = 0;
        try
        {
            for (var top = 0; top < outputHeight; top += bandHeight)
            {
                var rows = Math.Min(bandHeight, outputHeight - top);
                var firstInputRow = Math.Max(0, (2 * top) - 1);
                var lastInputRow = Math.Min(height - 1, (2 * (top + rows - 1)) + 2);
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
        FilterVerticalBytes(a, b, c, d, scratch[..length], height % 2 != 0);
        ReduceHorizontalBytes(scratch[..length], output, width, channels);
    }

    private static void FilterVerticalBytes(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, ReadOnlySpan<byte> c,
        ReadOnlySpan<byte> d, Span<byte> target, bool oddHeight)
    {
        var offset = 0;
        // SIMD는 여러 바이트에 같은 정수 계산을 한 번에 적용한다. 16비트로 넓혀 합산 오버플로를 막는다.
        if (Vector.IsHardwareAccelerated)
        {
            for (; offset <= target.Length - Vector<byte>.Count; offset += Vector<byte>.Count)
            {
                Vector.Widen(new Vector<byte>(a.Slice(offset)), out var aLow, out var aHigh);
                Vector.Widen(new Vector<byte>(b.Slice(offset)), out var bLow, out var bHigh);
                Vector.Widen(new Vector<byte>(c.Slice(offset)), out var cLow, out var cHigh);
                Vector.Widen(new Vector<byte>(d.Slice(offset)), out var dLow, out var dHigh);
                Vector.Narrow(Filter(aLow, bLow, cLow, dLow, oddHeight),
                    Filter(aHigh, bHigh, cHigh, dHigh, oddHeight)).CopyTo(target.Slice(offset));
            }
        }
        for (; offset < target.Length; offset++)
        {
            target[offset] = (byte)Filter(a[offset], b[offset], c[offset], d[offset], oddHeight);
        }
    }

    private static void ReduceHorizontalBytes(ReadOnlySpan<byte> scratch, Span<byte> output, int width, int channels)
    {
        for (var x = 0; x < output.Length / channels;)
        {
            // RGBA의 내부 픽셀 네 개를 처리한다. 가장자리/짧은 행/미지원 CPU는 아래 원래 계산을 쓴다.
            if (channels == 4 && Vector256.IsHardwareAccelerated && x > 0 && (2 * x) + 10 <= width)
            {
                var start = ((2 * x) - 1) * channels;
                var aa = GatherRgba(scratch.Slice(start));
                var bb = GatherRgba(scratch.Slice(start + 4));
                var cc = GatherRgba(scratch.Slice(start + 8));
                var dd = GatherRgba(scratch.Slice(start + 12));
                var filtered = width % 2 != 0
                    ? (aa + (bb << 1) + cc + Vector256.Create((ushort)2)) >> 2
                    : (aa + bb + (bb << 1) + cc + (cc << 1) + dd + Vector256.Create((ushort)4)) >> 3;
                Vector256.Narrow(filtered, Vector256<ushort>.Zero).GetLower().CopyTo(output.Slice(x * channels, 16));
                x += 4;
                continue;
            }
            for (var channel = 0; channel < channels; channel++)
            {
                output[(x * channels) + channel] = (byte)Filter(scratch[(Math.Max((2 * x) - 1, 0) * channels) + channel],
                    scratch[(Math.Min(2 * x, width - 1) * channels) + channel],
                    scratch[(Math.Min((2 * x) + 1, width - 1) * channels) + channel],
                    scratch[(Math.Min((2 * x) + 2, width - 1) * channels) + channel], width % 2 != 0);
            }
            x++;
        }
    }

    // 이웃한 출력 네 픽셀은 입력에서 한 픽셀 간격이다. RGBA 네 성분을 함께 꺼낸다.
    private static Vector256<ushort> GatherRgba(ReadOnlySpan<byte> row)
    {
        var pixels = Vector256.Create(row).AsUInt32();
        var selected = Vector256.Shuffle(pixels, Vector256.Create(0u, 2u, 4u, 6u, 0u, 2u, 4u, 6u));
        return Vector256.WidenLower(selected.AsByte());
    }

    private static Vector<ushort> Filter(Vector<ushort> a, Vector<ushort> b, Vector<ushort> c, Vector<ushort> d, bool oddLength) => oddLength
        ? (a + (b << 1) + c + new Vector<ushort>(2)) >> 2
        : (a + b + (b << 1) + c + (c << 1) + d + new Vector<ushort>(4)) >> 3;

    // 짝수 길이는 반 화소 중심, 홀수 길이는 정수 화소 중심이며 축마다 반올림한다.
    private static int Filter(int a, int b, int c, int d, bool oddLength) => oddLength
        ? (a + (2 * b) + c + 2) / 4
        : (a + (3 * b) + (3 * c) + d + 4) / 8;
}
