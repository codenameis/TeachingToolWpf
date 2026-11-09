// File: src/VisionAlgorithm/OpenSrc/EdgeFewShot/Backbone/GemSpatialPooler.cs
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace VisionAlgorithm.OpenSrc.EdgeFewShot.Backbone;

/// <summary>
/// Generalized Mean (GeM) spatial pooler (Radenovic et al., TPAMI 2018) and mid-layer feature aggregator
/// (PaDiM ICPR 2021 / PatchCore CVPR 2022):
/// <c>f_c = ( (1 / (H * W)) * sum_{u,v} max(x_{c,u,v}, epsilon)^p )^(1/p)</c>.
/// </summary>
public sealed class GemSpatialPooler
{
    // [Math Why]: Positive floor epsilon prevents undefined 0^p derivatives and NaN during fractional power pooling.
    public const float MinActivationEpsilon = 1e-6f;

    // [Math Why]: Spatial Pyramid GeM adds 2x2 symmetric quadrant bins on the ExcessPeakEnergy channel of Layer 3
    // so localized high-contrast defects in any quadrant (top-left, top-right, bottom-left, bottom-right) are separated symmetrically
    // without inflating normal edge/gradient channels that shift across quadrant midlines under minor pose rotation.
    public const int SpatialQuadrantChannelCount = 1;
    public const int SpatialQuadrantBinsPerChannel = 4;
    public const int SpatialPyramidExtraBins = SpatialQuadrantChannelCount * SpatialQuadrantBinsPerChannel;

    // [Math Why]: Tolerance below which p is treated as exact arithmetic mean (GAP, p = 1.0) to skip transcendental Pow calls.
    private const float UnitExponentTolerance = 1e-4f;
    private const float CubicExponent = 3.0f;

    public static int GetPooledFeatureDimension(int layer2Channels, int layer3Channels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(layer2Channels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(layer3Channels);
        var quadrantChannels = System.Math.Min(layer3Channels, SpatialQuadrantChannelCount);
        return checked(layer2Channels + layer3Channels + (quadrantChannels * SpatialQuadrantBinsPerChannel));
    }

    /// <summary>
    /// Pools planar <c>Layer 2 [C2, H2, W2]</c> and <c>Layer 3 [C3, H3, W3]</c> globally (<c>1x1</c>),
    /// and appends <c>2x2</c> symmetric Spatial Pyramid GeM quadrant bins for the anomaly energy channels of <c>Layer 3</c>
    /// when <paramref name="concatenatedFeatureOut"/> has capacity <see cref="GetPooledFeatureDimension"/>.
    /// </summary>
    public void PoolAndConcatenate(
        ReadOnlySpan<float> layer2Map,
        int layer2Channels,
        int layer2Width,
        int layer2Height,
        ReadOnlySpan<float> layer3Map,
        int layer3Channels,
        int layer3Width,
        int layer3Height,
        float exponentP,
        Span<float> concatenatedFeatureOut,
        Span<float> upsamplePlaneScratch = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(layer2Channels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(layer2Width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(layer2Height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(layer3Channels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(layer3Width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(layer3Height);
        if (!float.IsFinite(exponentP) || (exponentP < 1f))
        {
            throw new ArgumentOutOfRangeException(nameof(exponentP), "GeM exponent p must be finite and >= 1.0.");
        }

        var l2PlaneSize = checked(layer2Width * layer2Height);
        var l3PlaneSize = checked(layer3Width * layer3Height);
        if (layer2Map.Length < checked(layer2Channels * l2PlaneSize))
        {
            throw new ArgumentException("layer2Map span is shorter than C2 * H2 * W2.", nameof(layer2Map));
        }

        if (layer3Map.Length < checked(layer3Channels * l3PlaneSize))
        {
            throw new ArgumentException("layer3Map span is shorter than C3 * H3 * W3.", nameof(layer3Map));
        }

        var totalChannels = checked(layer2Channels + layer3Channels);
        if (concatenatedFeatureOut.Length < totalChannels)
        {
            throw new ArgumentException("concatenatedFeatureOut span is shorter than C2 + C3.", nameof(concatenatedFeatureOut));
        }

        for (var c = 0; c < layer2Channels; c++)
        {
            var plane = layer2Map.Slice(c * l2PlaneSize, l2PlaneSize);
            concatenatedFeatureOut[c] = PoolChannelPlane(plane, exponentP);
        }

        for (var c = 0; c < layer3Channels; c++)
        {
            var srcPlane = layer3Map.Slice(c * l3PlaneSize, l3PlaneSize);
            concatenatedFeatureOut[layer2Channels + c] = PoolChannelPlane(srcPlane, exponentP);
        }

        var spatialPyramidDim = GetPooledFeatureDimension(layer2Channels, layer3Channels);
        if (concatenatedFeatureOut.Length < spatialPyramidDim)
        {
            return;
        }

        var quadrantChannelCount = System.Math.Min(layer3Channels, SpatialQuadrantChannelCount);
        var startChannel = System.Math.Max(0, layer3Channels - quadrantChannelCount);
        var outIndex = totalChannels;
        for (var c = startChannel; c < layer3Channels; c++)
        {
            var srcPlane = layer3Map.Slice(c * l3PlaneSize, l3PlaneSize);
            PoolChannelQuadrants(srcPlane, layer3Width, layer3Height, exponentP, concatenatedFeatureOut.Slice(outIndex, SpatialQuadrantBinsPerChannel));
            outIndex += SpatialQuadrantBinsPerChannel;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void PoolChannelQuadrants(
        ReadOnlySpan<float> plane,
        int width,
        int height,
        float exponentP,
        Span<float> quadrantBinsOut)
    {
        var midX = System.Math.Max(1, width / 2);
        var midY = System.Math.Max(1, height / 2);
        var rightStart = width > 1 ? midX : 0;
        var bottomStart = height > 1 ? midY : 0;

        quadrantBinsOut[0] = PoolSubWindow(plane, width, 0, midX, 0, midY, exponentP);
        quadrantBinsOut[1] = PoolSubWindow(plane, width, rightStart, width, 0, midY, exponentP);
        quadrantBinsOut[2] = PoolSubWindow(plane, width, 0, midX, bottomStart, height, exponentP);
        quadrantBinsOut[3] = PoolSubWindow(plane, width, rightStart, width, bottomStart, height, exponentP);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float PoolSubWindow(
        ReadOnlySpan<float> plane,
        int stride,
        int xStart,
        int xEnd,
        int yStart,
        int yEnd,
        float exponentP)
    {
        var count = (xEnd - xStart) * (yEnd - yStart);
        if (count <= 0)
        {
            return MinActivationEpsilon;
        }

        ref var baseRef = ref MemoryMarshal.GetReference(plane);
        if (MathF.Abs(exponentP - 1f) <= UnitExponentTolerance)
        {
            var sum = 0f;
            for (var y = yStart; y < yEnd; y++)
            {
                var rowOffset = y * stride;
                for (var x = xStart; x < xEnd; x++)
                {
                    sum += MathF.Max(Unsafe.Add(ref baseRef, rowOffset + x), MinActivationEpsilon);
                }
            }

            return sum / count;
        }

        if (MathF.Abs(exponentP - CubicExponent) <= UnitExponentTolerance)
        {
            var sumCubes = 0f;
            for (var y = yStart; y < yEnd; y++)
            {
                var rowOffset = y * stride;
                for (var x = xStart; x < xEnd; x++)
                {
                    var v = MathF.Max(Unsafe.Add(ref baseRef, rowOffset + x), MinActivationEpsilon);
                    sumCubes += v * v * v;
                }
            }

            return MathF.Cbrt(sumCubes / count);
        }

        var sumPow = 0f;
        for (var y = yStart; y < yEnd; y++)
        {
            var rowOffset = y * stride;
            for (var x = xStart; x < xEnd; x++)
            {
                var v = MathF.Max(Unsafe.Add(ref baseRef, rowOffset + x), MinActivationEpsilon);
                sumPow += MathF.Pow(v, exponentP);
            }
        }

        return MathF.Pow(sumPow / count, 1f / exponentP);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public float PoolChannelPlane(ReadOnlySpan<float> channelPlane, float exponentP)
    {
        if (channelPlane.IsEmpty)
        {
            throw new ArgumentException("Channel plane must not be empty.", nameof(channelPlane));
        }

        if (!float.IsFinite(exponentP) || (exponentP < 1f))
        {
            throw new ArgumentOutOfRangeException(nameof(exponentP), "GeM exponent p must be >= 1.0.");
        }

        var count = channelPlane.Length;
        ref var baseRef = ref MemoryMarshal.GetReference(channelPlane);

        if (MathF.Abs(exponentP - 1f) <= UnitExponentTolerance)
        {
            return PoolArithmeticMeanSimd(ref baseRef, count);
        }

        if (MathF.Abs(exponentP - CubicExponent) <= UnitExponentTolerance)
        {
            return PoolCubicMeanSimd(ref baseRef, count);
        }

        var sumPow = 0f;
        for (var i = 0; i < count; i++)
        {
            var clamped = MathF.Max(Unsafe.Add(ref baseRef, i), MinActivationEpsilon);
            sumPow += MathF.Pow(clamped, exponentP);
        }

        return MathF.Pow(sumPow / count, 1f / exponentP);
    }

    // [Perf Why]: Fast path for p == 1.0 (GAP) using Vector256 clamped summation without transcendental MathF.Pow.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float PoolArithmeticMeanSimd(ref float baseRef, int count)
    {
        var i = 0;
        var sum = 0f;
        if (Vector256.IsHardwareAccelerated && (count >= Vector256<float>.Count))
        {
            var vecCount = Vector256<float>.Count;
            var limit = count & ~(vecCount - 1);
            var vEps = Vector256.Create(MinActivationEpsilon);
            var vSum = Vector256<float>.Zero;

            for (; i < limit; i += vecCount)
            {
                var v = Vector256.Max(Vector256.LoadUnsafe(ref baseRef, (nuint)i), vEps);
                vSum += v;
            }

            sum = Vector256.Sum(vSum);
        }

        for (; i < count; i++)
        {
            sum += MathF.Max(Unsafe.Add(ref baseRef, i), MinActivationEpsilon);
        }

        return sum / count;
    }

    // [Perf Why]: Fast path for default p == 3.0 using Vector256 x * x * x FMA accumulation instead of scalar MathF.Pow(x, 3).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float PoolCubicMeanSimd(ref float baseRef, int count)
    {
        var i = 0;
        var sumCubes = 0f;
        if (Vector256.IsHardwareAccelerated && (count >= Vector256<float>.Count))
        {
            var vecCount = Vector256<float>.Count;
            var limit = count & ~(vecCount - 1);
            var vEps = Vector256.Create(MinActivationEpsilon);
            var vSum = Vector256<float>.Zero;

            for (; i < limit; i += vecCount)
            {
                var v = Vector256.Max(Vector256.LoadUnsafe(ref baseRef, (nuint)i), vEps);
                var vSq = v * v;
                vSum = Fma.IsSupported ? Fma.MultiplyAdd(vSq, v, vSum) : vSum + (vSq * v);
            }

            sumCubes = Vector256.Sum(vSum);
        }

        for (; i < count; i++)
        {
            var v = MathF.Max(Unsafe.Add(ref baseRef, i), MinActivationEpsilon);
            sumCubes += v * v * v;
        }

        return MathF.Cbrt(sumCubes / count);
    }

    internal static void UpsamplePlaneBilinear(
        ReadOnlySpan<float> sourcePlane,
        int srcWidth,
        int srcHeight,
        Span<float> destinationPlane,
        int dstWidth,
        int dstHeight)
    {
        var scaleX = (float)srcWidth / dstWidth;
        var scaleY = (float)srcHeight / dstHeight;
        var maxSrcX = srcWidth - 1;
        var maxSrcY = srcHeight - 1;

        for (var y = 0; y < dstHeight; y++)
        {
            var sy = System.Math.Clamp(((y + 0.5f) * scaleY) - 0.5f, 0f, maxSrcY);
            var y0 = (int)MathF.Floor(sy);
            var y1 = System.Math.Min(y0 + 1, maxSrcY);
            var fy = sy - y0;
            var oneMinusFy = 1f - fy;

            var srcRow0 = y0 * srcWidth;
            var srcRow1 = y1 * srcWidth;
            var dstRow = y * dstWidth;

            for (var x = 0; x < dstWidth; x++)
            {
                var sx = System.Math.Clamp(((x + 0.5f) * scaleX) - 0.5f, 0f, maxSrcX);
                var x0 = (int)MathF.Floor(sx);
                var x1 = System.Math.Min(x0 + 1, maxSrcX);
                var fx = sx - x0;
                var oneMinusFx = 1f - fx;

                var top = (sourcePlane[srcRow0 + x0] * oneMinusFx) + (sourcePlane[srcRow0 + x1] * fx);
                var bottom = (sourcePlane[srcRow1 + x0] * oneMinusFx) + (sourcePlane[srcRow1 + x1] * fx);
                destinationPlane[dstRow + x] = (top * oneMinusFy) + (bottom * fy);
            }
        }
    }
}
