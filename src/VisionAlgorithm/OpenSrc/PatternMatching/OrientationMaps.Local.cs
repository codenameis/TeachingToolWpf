using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace VisionAlgorithm.OpenSrc.PatternMatching;

internal sealed partial class OrientationMaps
{
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal void AccumulateLocal(ImmutableArray<QuantizedFeature> features, int left, int top, int width, int height,
        Span<ushort> scores, bool simd = true)
    {
        scores.Clear();
        foreach (var feature in features)
        {
            var sourceLeft = left + feature.X;
            var from = Math.Max(0, -sourceLeft);
            var to = Math.Min(width, Width - sourceLeft);
            if (from >= to)
            {
                continue;
            }
            for (var row = 0; row < height; row++)
            {
                var y = top + row + feature.Y;
                if (y < 0 || y >= Height)
                {
                    continue;
                }
                var destination = scores.Slice((row * width) + from, to - from);
                var input = _spread.AsSpan((y * Width) + sourceLeft + from, to - from);
                var offset = 0;
                if (simd && Ssse3.IsSupported)
                {
                    var lower = LocalResponses.Lower[feature.Bin];
                    var upper = LocalResponses.Upper[feature.Bin];
                    var mask = Vector128.Create((byte)15);
                    for (; offset <= input.Length - 8; offset += 8)
                    {
                        var bits = Vector128.CreateScalar(MemoryMarshal.Read<ulong>(input.Slice(offset))).AsByte();
                        var low = Ssse3.Shuffle(lower, bits & mask);
                        var high = Ssse3.Shuffle(upper, Sse2.ShiftRightLogical(bits.AsUInt16(), 4).AsByte() & mask);
                        var response = Sse2.UnpackLow(Sse2.Max(low, high), Vector128<byte>.Zero).AsUInt16();
                        var previous = Vector128.LoadUnsafe(ref MemoryMarshal.GetReference(destination), (nuint)offset);
                        (previous + response).StoreUnsafe(ref MemoryMarshal.GetReference(destination), (nuint)offset);
                    }
                }
                for (; offset < input.Length; offset++)
                {
                    destination[offset] += s_responses[(feature.Bin * 256) + input[offset]];
                }
            }
        }
    }

    private static class LocalResponses
    {
        public static readonly Vector128<byte>[] Lower = Create(false);
        public static readonly Vector128<byte>[] Upper = Create(true);

        private static Vector128<byte>[] Create(bool high)
        {
            var result = new Vector128<byte>[8];
            Span<byte> entries = stackalloc byte[16];
            for (var bin = 0; bin < result.Length; bin++)
            {
                for (var value = 0; value < entries.Length; value++)
                {
                    entries[value] = s_responses[(bin * 256) + (high ? value << 4 : value)];
                }
                result[bin] = Vector128.Create(entries);
            }
            return result;
        }
    }
}
