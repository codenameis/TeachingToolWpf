using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using VisionAlgorithm;
using VisionAlgorithm.OpenSrc.PatternMatching;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Imaging.Matching;

/// <summary>원본 타일의 반복 2×2 반올림 평균을 독립 소유 결과 버퍼에 직접 기록한다.</summary>
internal static class InspectionTileReducer
{
    public static PixelBuffer Reduce(GrayImage source, int factor, CancellationToken token)
    {
        if (factor is < 1 or > 32 || (factor & (factor - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(factor));
        }
        token.ThrowIfCancellationRequested();
        var result = new PixelBuffer(Math.Max(1, source.Width / factor), Math.Max(1, source.Height / factor), PixelKind.Gray8);
        try
        {
            if (Avx2.IsSupported && factor is 2 or 4 && source.Width >= factor && source.Height >= factor)
            {
                if (factor == 2)
                {
                    ReduceTwo(source, result, token);
                }
                else
                {
                    ReduceFour(source, result, token);
                }
            }
            else
            {
                // 1~3 pixel의 좁은 가장자리는 기존 반복 축소의 clamp 규칙을 그대로 사용한다.
                var reduced = InspectionPyramid.Reduce(source, factor, token);
                for (var y = 0; y < result.Height; y++)
                {
                    token.ThrowIfCancellationRequested();
                    reduced.Pixels.Span.Slice(y * reduced.Stride, result.Width)
                        .CopyTo(result.Span.Slice(y * result.Stride, result.Width));
                }
            }
            token.ThrowIfCancellationRequested();
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void ReduceTwo(GrayImage source, PixelBuffer target, CancellationToken token)
    {
        var input = source.Pixels.Span;
        var output = target.Span;
        for (var y = 0; y < target.Height; y++)
        {
            token.ThrowIfCancellationRequested();
            var top = y * 2 * source.Stride;
            var destination = y * target.Width;
            var x = 0;
            for (; x <= target.Width - 16; x += 16)
            {
                var average = AveragePairRows(input, top + (x * 2), source.Stride);
                var packed = Avx2.PackUnsignedSaturate(average.AsInt16(), Vector256<short>.Zero);
                var bytes = Sse2.UnpackLow(packed.GetLower().AsUInt64(), packed.GetUpper().AsUInt64()).AsByte();
                bytes.StoreUnsafe(ref MemoryMarshal.GetReference(output), (nuint)(destination + x));
            }
            for (; x < target.Width; x++)
            {
                output[destination + x] = (byte)AverageSquare(input, top + (x * 2), source.Stride);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void ReduceFour(GrayImage source, PixelBuffer target, CancellationToken token)
    {
        var input = source.Pixels.Span;
        var output = target.Span;
        for (var y = 0; y < target.Height; y++)
        {
            token.ThrowIfCancellationRequested();
            var top = y * 4 * source.Stride;
            var lower = top + (2 * source.Stride);
            var destination = y * target.Width;
            var x = 0;
            for (; x <= target.Width - 8; x += 8)
            {
                // 원본 4×4의 단일 평균이 아니다. 먼저 각 2×2를 반올림한 뒤 다음 레벨도 반올림한다.
                var upperMeans = AveragePairRows(input, top + (x * 4), source.Stride);
                var lowerMeans = AveragePairRows(input, lower + (x * 4), source.Stride);
                var sumPairs = Avx2.MultiplyAddAdjacent((upperMeans + lowerMeans).AsInt16(), Vector256.Create((short)1));
                var means = (sumPairs + Vector256.Create(2)) >> 2;
                var words = Sse2.PackSignedSaturate(means.GetLower(), means.GetUpper());
                var bytes = Sse2.PackUnsignedSaturate(words, Vector128<short>.Zero).AsUInt64().GetElement(0);
                MemoryMarshal.Write(output.Slice(destination + x, 8), in bytes);
            }
            for (; x < target.Width; x++)
            {
                var upperLeft = top + (x * 4);
                var lowerLeft = lower + (x * 4);
                output[destination + x] = (byte)((AverageSquare(input, upperLeft, source.Stride) +
                    AverageSquare(input, upperLeft + 2, source.Stride) +
                    AverageSquare(input, lowerLeft, source.Stride) +
                    AverageSquare(input, lowerLeft + 2, source.Stride) + 2) >> 2);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<ushort> AveragePairRows(ReadOnlySpan<byte> source, int top, int stride)
    {
        var upper = Vector256.LoadUnsafe(ref MemoryMarshal.GetReference(source), (nuint)top);
        var lower = Vector256.LoadUnsafe(ref MemoryMarshal.GetReference(source), (nuint)(top + stride));
        var upperPairs = Avx2.MultiplyAddAdjacent(upper, Vector256.Create((sbyte)1)).AsUInt16();
        var lowerPairs = Avx2.MultiplyAddAdjacent(lower, Vector256.Create((sbyte)1)).AsUInt16();
        return (upperPairs + lowerPairs + Vector256.Create((ushort)2)) >> 2;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int AverageSquare(ReadOnlySpan<byte> source, int top, int stride)
        => (source[top] + source[top + 1] + source[top + stride] + source[top + stride + 1] + 2) >> 2;
}
