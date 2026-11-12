using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace VisionAlgorithm.OpenSrc.PatMax;

/// <summary>
/// Rotates, scales, and quantizes SoA dipole probe offsets into linear coarse-frame memory offsets using AVX2 SIMD,
/// and precomputes 32-bin 1-byte orientation compatibility bitmasks for anchor early-rejection.
/// </summary>
internal static class RotatedProbeTable
{
    public const int OrientationBinCount = 32;
    private const float TwoPi = MathF.PI * 2.0f;
    private const float OrientationBinsPerRadian = OrientationBinCount / TwoPi;
    private const int OrientationLutToleranceBins = 2;
    private const float MinNormalSquaredEpsilon = 1e-6f;

    internal readonly record struct ProbeOffsetBounds(
        int MinOffsetX,
        int MaxOffsetX,
        int MinOffsetY,
        int MaxOffsetY);

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static ProbeOffsetBounds Prepare(
        EdgeDipoleSoA dipoles,
        float angleRadians,
        float netScale,
        int coarseStride,
        Span<float> rotDx,
        Span<float> rotDy,
        Span<float> rotCos,
        Span<float> rotSin,
        Span<int> linearOffsets)
    {
        var count = dipoles.Count;
        var (sin, cos) = MathF.SinCos(angleRadians);
        var scaledCos = cos * netScale;
        var scaledSin = sin * netScale;

        var srcX = dipoles.X;
        var srcY = dipoles.Y;
        var srcCos = dipoles.CosTheta;
        var srcSin = dipoles.SinTheta;

        ref var xRef = ref MemoryMarshal.GetReference(srcX);
        ref var yRef = ref MemoryMarshal.GetReference(srcY);
        ref var cRef = ref MemoryMarshal.GetReference(srcCos);
        ref var sRef = ref MemoryMarshal.GetReference(srcSin);

        ref var dxRef = ref MemoryMarshal.GetReference(rotDx);
        ref var dyRef = ref MemoryMarshal.GetReference(rotDy);
        ref var rcRef = ref MemoryMarshal.GetReference(rotCos);
        ref var rsRef = ref MemoryMarshal.GetReference(rotSin);

        var i = 0;
        // [Perf Why]: Rotate 8 dipole offsets and normal vectors simultaneously in AVX2 registers.
        if (Avx2.IsSupported)
        {
            var simdLimit = count & ~(Vector256<float>.Count - 1);
            var vScaledCos = Vector256.Create(scaledCos);
            var vScaledSin = Vector256.Create(scaledSin);
            var vCos = Vector256.Create(cos);
            var vSin = Vector256.Create(sin);

            for (; i < simdLimit; i += Vector256<float>.Count)
            {
                var vx = Vector256.LoadUnsafe(ref Unsafe.Add(ref xRef, i));
                var vy = Vector256.LoadUnsafe(ref Unsafe.Add(ref yRef, i));
                var vc = Vector256.LoadUnsafe(ref Unsafe.Add(ref cRef, i));
                var vs = Vector256.LoadUnsafe(ref Unsafe.Add(ref sRef, i));

                var rdx = (vx * vScaledCos) - (vy * vScaledSin);
                var rdy = (vx * vScaledSin) + (vy * vScaledCos);
                var rcos = (vc * vCos) - (vs * vSin);
                var rsin = (vc * vSin) + (vs * vCos);

                rdx.StoreUnsafe(ref Unsafe.Add(ref dxRef, i));
                rdy.StoreUnsafe(ref Unsafe.Add(ref dyRef, i));
                rcos.StoreUnsafe(ref Unsafe.Add(ref rcRef, i));
                rsin.StoreUnsafe(ref Unsafe.Add(ref rsRef, i));
            }
        }

        for (; i < count; i++)
        {
            var x = Unsafe.Add(ref xRef, i);
            var y = Unsafe.Add(ref yRef, i);
            var c = Unsafe.Add(ref cRef, i);
            var s = Unsafe.Add(ref sRef, i);

            Unsafe.Add(ref dxRef, i) = (x * scaledCos) - (y * scaledSin);
            Unsafe.Add(ref dyRef, i) = (x * scaledSin) + (y * scaledCos);
            Unsafe.Add(ref rcRef, i) = (c * cos) - (s * sin);
            Unsafe.Add(ref rsRef, i) = (c * sin) + (s * cos);
        }

        var minOffsetX = int.MaxValue;
        var maxOffsetX = int.MinValue;
        var minOffsetY = int.MaxValue;
        var maxOffsetY = int.MinValue;

        // [Math Why]: Map model probe offsets into integer coarse grid delta using unbiased round-half-up (Floor(x + 0.5f)) rather than Round-to-Even.
        for (var k = 0; k < count; k++)
        {
            var ox = (int)MathF.Floor(rotDx[k] + 0.5f);
            var oy = (int)MathF.Floor(rotDy[k] + 0.5f);
            if (ox < minOffsetX)
            {
                minOffsetX = ox;
            }

            if (ox > maxOffsetX)
            {
                maxOffsetX = ox;
            }

            if (oy < minOffsetY)
            {
                minOffsetY = oy;
            }

            if (oy > maxOffsetY)
            {
                maxOffsetY = oy;
            }

            linearOffsets[k] = (oy * coarseStride) + ox;
        }

        return new ProbeOffsetBounds(minOffsetX, maxOffsetX, minOffsetY, maxOffsetY);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void BuildQuantizedOrientationBins(
        ReadOnlySpan<float> unitX,
        ReadOnlySpan<float> unitY,
        Span<byte> orientBins)
    {
        var count = orientBins.Length;
        ref var uxRef = ref MemoryMarshal.GetReference(unitX);
        ref var uyRef = ref MemoryMarshal.GetReference(unitY);
        ref var binRef = ref MemoryMarshal.GetReference(orientBins);

        for (var i = 0; i < count; i++)
        {
            var ux = Unsafe.Add(ref uxRef, i);
            var uy = Unsafe.Add(ref uyRef, i);
            if ((ux * ux) + (uy * uy) <= MinNormalSquaredEpsilon)
            {
                Unsafe.Add(ref binRef, i) = 0;
                continue;
            }

            Unsafe.Add(ref binRef, i) = QuantizeNormalToBin32(ux, uy);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte QuantizeNormalToBin32(float ux, float uy)
    {
        var angle = MathF.Atan2(uy, ux);
        if (angle < 0.0f)
        {
            angle += TwoPi;
        }

        var bin = (int)MathF.Floor((angle * OrientationBinsPerRadian) + 0.5f) & (OrientationBinCount - 1);
        return (byte)(bin + 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint BuildAnchorOrientationMask(float cos, float sin, bool ignorePolarity)
    {
        var centerBinZeroBased = QuantizeNormalToBin32(cos, sin) - 1;
        var mask = 0u;
        for (var d = -OrientationLutToleranceBins; d <= OrientationLutToleranceBins; d++)
        {
            var b = (centerBinZeroBased + d) & (OrientationBinCount - 1);
            mask |= 1u << b;
            if (ignorePolarity)
            {
                var opp = (b + (OrientationBinCount >> 1)) & (OrientationBinCount - 1);
                mask |= 1u << opp;
            }
        }

        return mask;
    }
}
