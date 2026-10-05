using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using TeachingToolWpf.Core.Imaging;

namespace TeachingToolWpf.Imaging.Matching;

/// <summary>검사 명암 정책의 정수 변환. SIMD와 scalar는 상위 8비트·고정 BT.601 가중치에서 정확히 같다.</summary>
internal static class InspectionGrayConverter
{
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void ConvertRow(ReadOnlySpan<byte> source, Span<byte> destination, PixelKind kind, bool useSimd = true)
    {
        if (kind == PixelKind.Gray8)
        {
            source.CopyTo(destination);
            return;
        }
        var x = 0;
        if (kind == PixelKind.Gray16)
        {
            if (useSimd && Vector256.IsHardwareAccelerated)
            {
                for (; x <= destination.Length - 16; x += 16)
                {
                    var words = Vector256.LoadUnsafe(ref MemoryMarshal.GetReference(source), (nuint)(x * 2)).AsUInt16();
                    var bytes = Vector256.Narrow(words >> 8, Vector256<ushort>.Zero).GetLower();
                    bytes.StoreUnsafe(ref MemoryMarshal.GetReference(destination), (nuint)x);
                }
            }
            for (; x < destination.Length; x++)
            {
                destination[x] = source[(x * 2) + 1];
            }
            return;
        }
        if (kind is not PixelKind.Rgba32 and not PixelKind.Bgra32)
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }
        var firstWeight = kind == PixelKind.Rgba32 ? 77u : 29u;
        var thirdWeight = kind == PixelKind.Rgba32 ? 29u : 77u;
        if (useSimd && Avx2.IsSupported)
        {
            // 150은 signed byte에 들어가지 않는다. 22G + 128G로 나누면
            // maddubs의 각 signed 합은 최대 25245이고, 이후 unsigned 합도 65408 이하이다.
            var weights = Vector256.Create(firstWeight | (22u << 8) | (thirdWeight << 16)).AsSByte();
            var greenMask = Vector256.Create(255u);
            var rounding = Vector256.Create((ushort)128);
            for (; x <= destination.Length - 16; x += 16)
            {
                var first = Vector256.LoadUnsafe(ref MemoryMarshal.GetReference(source), (nuint)(x * 4));
                var second = Vector256.LoadUnsafe(ref MemoryMarshal.GetReference(source), (nuint)((x + 8) * 4));
                var firstPairs = Avx2.MultiplyAddAdjacent(first, weights);
                var secondPairs = Avx2.MultiplyAddAdjacent(second, weights);
                var partial = Avx2.HorizontalAdd(firstPairs, secondPairs).AsUInt16();
                var firstGreen = (first.AsUInt32() >> 8) & greenMask;
                var secondGreen = (second.AsUInt32() >> 8) & greenMask;
                var greens = Avx2.PackUnsignedSaturate(firstGreen.AsInt32(), secondGreen.AsInt32());
                var gray = (partial + (greens << 7) + rounding) >> 8;
                var packed = Avx2.PackUnsignedSaturate(gray.AsInt16(), Vector256<short>.Zero);
                // AVX2 연산의 128-bit lane 순서 [0..3,8..11]/[4..7,12..15]를 원래 순서로 복원한다.
                var bytes = Sse2.UnpackLow(packed.GetLower().AsUInt32(), packed.GetUpper().AsUInt32()).AsByte();
                bytes.StoreUnsafe(ref MemoryMarshal.GetReference(destination), (nuint)x);
            }
        }
        if (useSimd && Vector256.IsHardwareAccelerated)
        {
            var mask = Vector256.Create(255u);
            var firstWeights = Vector256.Create(firstWeight);
            var middleWeights = Vector256.Create(150u);
            var thirdWeights = Vector256.Create(thirdWeight);
            var rounding = Vector256.Create(128u);
            for (; x <= destination.Length - 8; x += 8)
            {
                var rgba = Vector256.LoadUnsafe(ref MemoryMarshal.GetReference(source), (nuint)(x * 4)).AsUInt32();
                var sum = ((rgba & mask) * firstWeights) + (((rgba >> 8) & mask) * middleWeights) +
                    (((rgba >> 16) & mask) * thirdWeights) + rounding;
                var words = Vector256.Narrow(sum >> 8, Vector256<uint>.Zero);
                var bytes = Vector256.Narrow(words, Vector256<ushort>.Zero).AsUInt64().GetElement(0);
                MemoryMarshal.Write(destination.Slice(x, 8), in bytes);
            }
        }
        for (; x < destination.Length; x++)
        {
            var index = x * 4;
            destination[x] = (byte)(((firstWeight * source[index]) + (150u * source[index + 1]) +
                (thirdWeight * source[index + 2]) + 128u) >> 8);
        }
    }
}
