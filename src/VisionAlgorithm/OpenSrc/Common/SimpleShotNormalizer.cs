// File: src/VisionAlgorithm/OpenSrc/Common/SimpleShotNormalizer.cs
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace VisionAlgorithm.OpenSrc.Common;

/// <summary>
/// Implements Generalized Mean (GeM) spatial pooling (Radenović et al., IEEE TPAMI 2018) with signed ReLU decomposition
/// and SimpleShot feature normalization (Wang et al., 2019: dataset mean subtraction $\bar{x}$ followed by unit $\ell_2$ normalization).
/// Serves as the single source of truth for both <c>Common</c> and <c>EdgeFewShot</c> normalization pipelines.
/// </summary>
public static class SimpleShotNormalizer
{
    // [Math Why]: Small positive floor epsilon prevents zero-power NaN derivatives in GeM pooling.
    private const float NumericalEpsilon = 1e-6f;

    // [Math Why]: Floor on squared L2 norm (1e-12 = (1e-6)^2) prevents NaN/Inf when a feature vector coincides with the global mean.
    public const float MinSquaredNormEpsilon = 1e-12f;

    // [Math Why]: Canonical GeM pooling exponent p = 3.0 focuses spatial pooling on salient high-activation structures
    // while admitting fast multiplication (v * v * v) and exact cube-root evaluation.
    private const float DefaultGemPower = 3.0f;

    // [Math Why]: Tolerance for detecting whether the caller requested the fast cubic GeM exponent (p = 3.0).
    private const float CubicExponentTolerance = 1e-4f;

    // [Perf Why]: AVX2 processes 8 float lanes per SIMD instruction.
    private const int Avx2LaneCount = 8;

    /// <summary>
    /// Pools a multi-channel spatial feature map $[C \times (H \times W)]$ using signed Generalized Mean (GeM) pooling
    /// into a vector of length $2C$ (positive and negative ReLU branches).
    /// </summary>
    public static void PoolSignedGem(
        ReadOnlySpan<float> featureMaps,
        int channelCount,
        int spatialLength,
        Span<float> pooledFeaturesOut,
        float gemPower = DefaultGemPower)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(channelCount, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(spatialLength, 0);
        if (featureMaps.Length < checked(channelCount * spatialLength))
        {
            throw new ArgumentException("featureMaps buffer is smaller than channelCount * spatialLength.", nameof(featureMaps));
        }

        if (pooledFeaturesOut.Length < checked(channelCount * 2))
        {
            throw new ArgumentException("pooledFeaturesOut length must be >= 2 * channelCount.", nameof(pooledFeaturesOut));
        }

        float p = MathF.Max(1f, gemPower);
        float invP = 1f / p;
        float invSpatial = 1f / spatialLength;
        bool isCubic = MathF.Abs(p - DefaultGemPower) <= CubicExponentTolerance;

        for (int c = 0; c < channelCount; c++)
        {
            ReadOnlySpan<float> channel = featureMaps.Slice(c * spatialLength, spatialLength);
            double sumPos = 0.0;
            double sumNeg = 0.0;

            for (int i = 0; i < spatialLength; i++)
            {
                float val = channel[i];
                if (val >= 0f)
                {
                    float v = val + NumericalEpsilon;
                    sumPos += isCubic ? (double)v * v * v : Math.Pow(v, p);
                }
                else
                {
                    float v = (-val) + NumericalEpsilon;
                    sumNeg += isCubic ? (double)v * v * v : Math.Pow(v, p);
                }
            }

            double meanPos = sumPos * invSpatial;
            double meanNeg = sumNeg * invSpatial;

            pooledFeaturesOut[2 * c] = (float)(isCubic ? Math.Cbrt(meanPos) : Math.Pow(meanPos, invP));
            pooledFeaturesOut[(2 * c) + 1] = (float)(isCubic ? Math.Cbrt(meanNeg) : Math.Pow(meanNeg, invP));
        }
    }

    /// <summary>
    /// Computes the global mean vector $\bar{x} = \frac{1}{N} \sum_{s=1}^N x_s$ across a flattened row-major feature matrix $[N \times D]$.
    /// </summary>
    public static void ComputeGlobalMean(
        ReadOnlySpan<float> flattenedFeatures,
        int sampleCount,
        int featureDimension,
        Span<float> globalMeanOut)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(featureDimension);

        int expectedTotal = checked(sampleCount * featureDimension);
        if (flattenedFeatures.Length < expectedTotal)
        {
            throw new ArgumentException("Flattened feature span is shorter than sampleCount * featureDimension.", nameof(flattenedFeatures));
        }

        if (globalMeanOut.Length < featureDimension)
        {
            throw new ArgumentException("Global mean output span is shorter than featureDimension.", nameof(globalMeanOut));
        }

        Span<float> meanSlice = globalMeanOut.Slice(0, featureDimension);
        meanSlice.Clear();

        for (int s = 0; s < sampleCount; s++)
        {
            ReadOnlySpan<float> sampleRow = flattenedFeatures.Slice(s * featureDimension, featureDimension);
            AccumulateVectorInto(sampleRow, meanSlice);
        }

        float invCount = 1f / sampleCount;
        ScaleInPlace(meanSlice, invCount);
    }

    /// <summary>
    /// Applies SimpleShot centering and $\ell_2$ normalization: $\tilde{x} = (x - \bar{x}) / \|x - \bar{x}\|_2$.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void CenterAndL2Normalize(
        ReadOnlySpan<float> rawFeature,
        ReadOnlySpan<float> datasetMean,
        Span<float> normalizedOut)
    {
        if (rawFeature.IsEmpty)
        {
            throw new ArgumentException("Feature vector must not be empty.", nameof(rawFeature));
        }

        int d = rawFeature.Length;
        if (datasetMean.Length < d || normalizedOut.Length < d)
        {
            throw new ArgumentException("datasetMean and normalizedOut must have length >= rawFeature.Length.");
        }

        float sumSq = 0f;
        int i = 0;

        if (Vector256.IsHardwareAccelerated && d >= Avx2LaneCount)
        {
            Vector256<float> vAcc = Vector256<float>.Zero;
            ref float rawRef = ref MemoryMarshal.GetReference(rawFeature);
            ref float meanRef = ref MemoryMarshal.GetReference(datasetMean);
            ref float outRef = ref MemoryMarshal.GetReference(normalizedOut);

            int simdLimit = d - (d % Avx2LaneCount);
            for (; i < simdLimit; i += Avx2LaneCount)
            {
                Vector256<float> vRaw = Vector256.LoadUnsafe(ref rawRef, (nuint)i);
                Vector256<float> vMean = Vector256.LoadUnsafe(ref meanRef, (nuint)i);
                Vector256<float> vDiff = vRaw - vMean;

                vDiff.StoreUnsafe(ref outRef, (nuint)i);
                vAcc = Fma.IsSupported ? Fma.MultiplyAdd(vDiff, vDiff, vAcc) : vAcc + (vDiff * vDiff);
            }

            sumSq = Vector256.Sum(vAcc);
        }

        for (; i < d; i++)
        {
            float diff = rawFeature[i] - datasetMean[i];
            normalizedOut[i] = diff;
            sumSq += diff * diff;
        }

        Span<float> dstSlice = normalizedOut.Slice(0, d);
        if (sumSq <= MinSquaredNormEpsilon)
        {
            dstSlice.Clear();
            return;
        }

        float invNorm = 1f / MathF.Sqrt(sumSq);
        ScaleInPlace(dstSlice, invNorm);
    }

    /// <summary>Normalizes a vector in-place to unit $\ell_2$ norm.</summary>
    public static void L2NormalizeInPlace(Span<float> vector)
    {
        if (vector.IsEmpty)
        {
            throw new ArgumentException("Vector must not be empty.", nameof(vector));
        }

        float sumSq = ComputeSquaredL2Norm(vector);
        if (sumSq <= MinSquaredNormEpsilon)
        {
            vector.Clear();
            return;
        }

        float invNorm = 1f / MathF.Sqrt(sumSq);
        ScaleInPlace(vector, invNorm);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static float ComputeSquaredL2Norm(ReadOnlySpan<float> vector)
    {
        int count = vector.Length;
        ref float baseRef = ref MemoryMarshal.GetReference(vector);

        int i = 0;
        float sqSum = 0f;
        if (Vector256.IsHardwareAccelerated && count >= Avx2LaneCount)
        {
            int limit = count - (count % Avx2LaneCount);
            Vector256<float> vSq = Vector256<float>.Zero;
            for (; i < limit; i += Avx2LaneCount)
            {
                Vector256<float> v = Vector256.LoadUnsafe(ref baseRef, (nuint)i);
                vSq = Fma.IsSupported ? Fma.MultiplyAdd(v, v, vSq) : vSq + (v * v);
            }

            sqSum = Vector256.Sum(vSq);
        }

        for (; i < count; i++)
        {
            float val = Unsafe.Add(ref baseRef, i);
            sqSum += val * val;
        }

        return sqSum;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void AccumulateVectorInto(ReadOnlySpan<float> source, Span<float> accumulator)
    {
        int count = source.Length;
        ref float srcBase = ref MemoryMarshal.GetReference(source);
        ref float accBase = ref MemoryMarshal.GetReference(accumulator);

        int i = 0;
        if (Vector256.IsHardwareAccelerated && count >= Avx2LaneCount)
        {
            int limit = count - (count % Avx2LaneCount);
            for (; i < limit; i += Avx2LaneCount)
            {
                Vector256<float> vAcc = Vector256.LoadUnsafe(ref accBase, (nuint)i);
                Vector256<float> vSrc = Vector256.LoadUnsafe(ref srcBase, (nuint)i);
                (vAcc + vSrc).StoreUnsafe(ref accBase, (nuint)i);
            }
        }

        for (; i < count; i++)
        {
            Unsafe.Add(ref accBase, i) += Unsafe.Add(ref srcBase, i);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void ScaleInPlace(Span<float> vector, float scale)
    {
        int d = vector.Length;
        int i = 0;
        if (Vector256.IsHardwareAccelerated && d >= Avx2LaneCount)
        {
            Vector256<float> vScale = Vector256.Create(scale);
            ref float vRef = ref MemoryMarshal.GetReference(vector);
            int simdLimit = d - (d % Avx2LaneCount);
            for (; i < simdLimit; i += Avx2LaneCount)
            {
                Vector256<float> v = Vector256.LoadUnsafe(ref vRef, (nuint)i) * vScale;
                v.StoreUnsafe(ref vRef, (nuint)i);
            }
        }

        for (; i < d; i++)
        {
            vector[i] *= scale;
        }
    }
}
