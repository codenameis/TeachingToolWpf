// File: src/VisionAlgorithm/OpenSrc/EdgeFewShot/Math/KMeansPlusPlusClusterer.cs
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace VisionAlgorithm.OpenSrc.EdgeFewShot.Math;

/// <summary>
/// Deterministic fixed-seed K-Means++ sub-clusterer (Arthur &amp; Vassilvitskii 2007)
/// for capturing multi-modal defect sub-prototypes within a single class.
/// </summary>
public sealed class KMeansPlusPlusClusterer
{
    public const int DefaultMaxIterations = 32;

    // [Math Why]: Knuth/PCG 64-bit LCG multiplier and increment provide deterministic cross-platform seeding without heap allocation.
    private const ulong DeterministicLcgMultiplier = 6364136223846793005UL;
    private const ulong DeterministicLcgIncrement = 1442695040888963407UL;
    private const int FloatMantissaShift = 40;
    private const float UnitFloatScale = 1.0f / (1UL << 24);

    public int Cluster(
        ReadOnlySpan<float> classSamples,
        int sampleCount,
        int dimension,
        int requestedClusters,
        int seed,
        Span<float> centroidsOut,
        Span<int> assignmentsOut,
        int maxIterations = DefaultMaxIterations)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dimension);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requestedClusters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxIterations);

        var expectedSampleFloats = checked(sampleCount * dimension);
        if (classSamples.Length < expectedSampleFloats)
        {
            throw new ArgumentException("classSamples span is smaller than sampleCount * dimension.", nameof(classSamples));
        }

        var actualClusters = System.Math.Min(requestedClusters, sampleCount);
        var expectedCentroidFloats = checked(actualClusters * dimension);
        if (centroidsOut.Length < expectedCentroidFloats)
        {
            throw new ArgumentException("centroidsOut span is smaller than actualClusters * dimension.", nameof(centroidsOut));
        }

        if (assignmentsOut.Length < sampleCount)
        {
            throw new ArgumentException("assignmentsOut span is smaller than sampleCount.", nameof(assignmentsOut));
        }

        var centroids = centroidsOut.Slice(0, expectedCentroidFloats);
        var assignments = assignmentsOut.Slice(0, sampleCount);

        if (actualClusters == 1)
        {
            centroids.Clear();
            for (var s = 0; s < sampleCount; s++)
            {
                assignments[s] = 0;
                SimpleShotNormalizer.AccumulateVectorInto(classSamples.Slice(s * dimension, dimension), centroids);
            }

            SimpleShotNormalizer.ScaleInPlace(centroids, 1f / sampleCount);
            NormalizeCentroidIfUnitSphere(classSamples.Slice(0, dimension), centroids);
            return 1;
        }

        var minSqDistancesPool = ArrayPool<float>.Shared.Rent(sampleCount);
        var clusterCountsPool = ArrayPool<int>.Shared.Rent(actualClusters);
        var nextCentroidsPool = ArrayPool<float>.Shared.Rent(expectedCentroidFloats);

        try
        {
            var minSqDistances = minSqDistancesPool.AsSpan(0, sampleCount);
            var clusterCounts = clusterCountsPool.AsSpan(0, actualClusters);
            var nextCentroids = nextCentroidsPool.AsSpan(0, expectedCentroidFloats);

            SeedKMeansPlusPlus(classSamples, sampleCount, dimension, actualClusters, seed, centroids, minSqDistances);

            for (var iter = 0; iter < maxIterations; iter++)
            {
                var changed = AssignSamplesToNearestCentroids(classSamples, sampleCount, dimension, centroids, actualClusters, assignments, iter == 0);
                if (!changed && (iter > 0))
                {
                    break;
                }

                nextCentroids.Clear();
                clusterCounts.Clear();

                for (var s = 0; s < sampleCount; s++)
                {
                    var clusterIdx = assignments[s];
                    clusterCounts[clusterIdx]++;
                    SimpleShotNormalizer.AccumulateVectorInto(
                        classSamples.Slice(s * dimension, dimension),
                        nextCentroids.Slice(clusterIdx * dimension, dimension));
                }

                for (var c = 0; c < actualClusters; c++)
                {
                    var centroidSlice = centroids.Slice(c * dimension, dimension);
                    var count = clusterCounts[c];
                    if (count > 0)
                    {
                        var nextSlice = nextCentroids.Slice(c * dimension, dimension);
                        SimpleShotNormalizer.ScaleInPlace(nextSlice, 1f / count);
                        NormalizeCentroidIfUnitSphere(classSamples.Slice(0, dimension), nextSlice);
                        nextSlice.CopyTo(centroidSlice);
                    }
                }
            }

            return actualClusters;
        }
        finally
        {
            ArrayPool<float>.Shared.Return(minSqDistancesPool);
            ArrayPool<int>.Shared.Return(clusterCountsPool);
            ArrayPool<float>.Shared.Return(nextCentroidsPool);
        }
    }

    private static void SeedKMeansPlusPlus(
        ReadOnlySpan<float> samples,
        int sampleCount,
        int dimension,
        int clusterCount,
        int seed,
        Span<float> centroids,
        Span<float> minSqDistances)
    {
        var rngState = ((ulong)(uint)seed * DeterministicLcgMultiplier) + DeterministicLcgIncrement;
        var firstIdx = (int)(NextUnitFloat(ref rngState) * sampleCount);
        if (firstIdx >= sampleCount)
        {
            firstIdx = sampleCount - 1;
        }

        samples.Slice(firstIdx * dimension, dimension).CopyTo(centroids.Slice(0, dimension));

        for (var s = 0; s < sampleCount; s++)
        {
            minSqDistances[s] = ComputeSquaredEuclideanDistance(
                samples.Slice(s * dimension, dimension),
                centroids.Slice(0, dimension));
        }

        for (var c = 1; c < clusterCount; c++)
        {
            var totalWeight = 0f;
            for (var s = 0; s < sampleCount; s++)
            {
                totalWeight += minSqDistances[s];
            }

            var chosenSample = 0;
            if (totalWeight <= SimpleShotNormalizer.MinNormEpsilon)
            {
                chosenSample = c % sampleCount;
            }
            else
            {
                var target = NextUnitFloat(ref rngState) * totalWeight;
                var cumulative = 0f;
                chosenSample = sampleCount - 1;
                for (var s = 0; s < sampleCount; s++)
                {
                    cumulative += minSqDistances[s];
                    if (cumulative >= target)
                    {
                        chosenSample = s;
                        break;
                    }
                }
            }

            var newCentroid = centroids.Slice(c * dimension, dimension);
            samples.Slice(chosenSample * dimension, dimension).CopyTo(newCentroid);

            for (var s = 0; s < sampleCount; s++)
            {
                var distSq = ComputeSquaredEuclideanDistance(samples.Slice(s * dimension, dimension), newCentroid);
                if (distSq < minSqDistances[s])
                {
                    minSqDistances[s] = distSq;
                }
            }
        }
    }

    private static bool AssignSamplesToNearestCentroids(
        ReadOnlySpan<float> samples,
        int sampleCount,
        int dimension,
        ReadOnlySpan<float> centroids,
        int clusterCount,
        Span<int> assignments,
        bool isFirstIteration)
    {
        var changed = false;
        for (var s = 0; s < sampleCount; s++)
        {
            var sample = samples.Slice(s * dimension, dimension);
            var bestCluster = 0;
            var bestDistSq = ComputeSquaredEuclideanDistance(sample, centroids.Slice(0, dimension));

            for (var c = 1; c < clusterCount; c++)
            {
                var distSq = ComputeSquaredEuclideanDistance(sample, centroids.Slice(c * dimension, dimension));
                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    bestCluster = c;
                }
            }

            if (isFirstIteration || (assignments[s] != bestCluster))
            {
                assignments[s] = bestCluster;
                changed = true;
            }
        }

        return changed;
    }

    // [Math Why]: When input features lie on the unit hypersphere (SimpleShot), re-projecting the centroid preserves unit-norm geometry.
    private static void NormalizeCentroidIfUnitSphere(ReadOnlySpan<float> referenceSample, Span<float> centroid)
    {
        var refNormSq = SimpleShotNormalizer.ComputeSquaredL2Norm(referenceSample);
        const float UnitNormTolerance = 0.05f;
        if (MathF.Abs(refNormSq - 1f) <= UnitNormTolerance)
        {
            var centroidNormSq = SimpleShotNormalizer.ComputeSquaredL2Norm(centroid);
            if (centroidNormSq > SimpleShotNormalizer.MinNormEpsilon)
            {
                SimpleShotNormalizer.ScaleInPlace(centroid, 1f / MathF.Sqrt(centroidNormSq));
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static float ComputeSquaredEuclideanDistance(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var count = a.Length;
        ref var aBase = ref MemoryMarshal.GetReference(a);
        ref var bBase = ref MemoryMarshal.GetReference(b);

        var i = 0;
        var sqSum = 0f;
        if (Vector256.IsHardwareAccelerated && (count >= Vector256<float>.Count))
        {
            var vecCount = Vector256<float>.Count;
            var limit = count & ~(vecCount - 1);
            var vSq = Vector256<float>.Zero;
            for (; i < limit; i += vecCount)
            {
                var diff = Vector256.LoadUnsafe(ref aBase, (nuint)i) - Vector256.LoadUnsafe(ref bBase, (nuint)i);
                vSq = Fma.IsSupported ? Fma.MultiplyAdd(diff, diff, vSq) : vSq + (diff * diff);
            }

            sqSum = Vector256.Sum(vSq);
        }

        for (; i < count; i++)
        {
            var diff = Unsafe.Add(ref aBase, i) - Unsafe.Add(ref bBase, i);
            sqSum += diff * diff;
        }

        return sqSum;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float NextUnitFloat(ref ulong state)
    {
        state = (state * DeterministicLcgMultiplier) + DeterministicLcgIncrement;
        return (state >> FloatMantissaShift) * UnitFloatScale;
    }
}
