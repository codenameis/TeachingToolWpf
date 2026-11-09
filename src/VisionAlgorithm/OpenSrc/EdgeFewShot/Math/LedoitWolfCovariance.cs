// File: src/VisionAlgorithm/OpenSrc/EdgeFewShot/Math/LedoitWolfCovariance.cs
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace VisionAlgorithm.OpenSrc.EdgeFewShot.Math;

/// <summary>
/// Ledoit-Wolf (2004) / SimpleCNAPS (CVPR 2020) shrinkage covariance estimator and Cholesky solver:
/// <c>Sigma_reg = (1 - lambda) * Sigma_tied + lambda * (Tr(Sigma_tied) / D) * I_D = L * L^T</c>.
/// Precomputes lower-triangular inverse factor <c>L^-1</c> at train time so inference Mahalanobis distance
/// <c>d_M(x, mu) = || L^-1 (x - mu) ||_2</c> executes via SIMD row dot products with zero heap allocation.
/// </summary>
public sealed class LedoitWolfCovariance
{
    // [Math Why]: Minimum spherical target variance when all training residuals are identical (zero sample trace).
    public const float MinIsotropicDiagonalVariance = 1e-4f;

    // [Math Why]: Strictly positive pivot threshold enforcing SPD invariant L_ii > 0 during Cholesky decomposition.
    public const float MinCholeskyPivot = 1e-7f;

    public void EstimateRegularizedCholeskyInverse(
        ReadOnlySpan<float> residuals,
        int sampleCount,
        int dimension,
        float shrinkageLambda,
        Span<float> choleskyLowerOut,
        Span<float> choleskyInverseOut,
        int degreesOfFreedom = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dimension);
        if (!float.IsFinite(shrinkageLambda) || (shrinkageLambda <= 0f) || (shrinkageLambda >= 1f))
        {
            throw new ArgumentOutOfRangeException(nameof(shrinkageLambda), "Shrinkage lambda must be in (0, 1).");
        }

        var expectedResidualFloats = checked(sampleCount * dimension);
        if (residuals.Length < expectedResidualFloats)
        {
            throw new ArgumentException("Residuals span is smaller than sampleCount * dimension.", nameof(residuals));
        }

        var matrixFloats = checked(dimension * dimension);
        if (choleskyLowerOut.Length < matrixFloats)
        {
            throw new ArgumentException("Cholesky lower matrix span is smaller than dimension * dimension.", nameof(choleskyLowerOut));
        }

        if (choleskyInverseOut.Length < matrixFloats)
        {
            throw new ArgumentException("Cholesky inverse matrix span is smaller than dimension * dimension.", nameof(choleskyInverseOut));
        }

        var covariancePool = ArrayPool<float>.Shared.Rent(matrixFloats);
        try
        {
            var covariance = covariancePool.AsSpan(0, matrixFloats);
            ComputeRegularizedCovariance(residuals.Slice(0, expectedResidualFloats), sampleCount, dimension, shrinkageLambda, covariance, degreesOfFreedom);
            DecomposeCholesky(covariance, dimension, choleskyLowerOut.Slice(0, matrixFloats));
            InvertLowerTriangular(choleskyLowerOut.Slice(0, matrixFloats), dimension, choleskyInverseOut.Slice(0, matrixFloats));
        }
        finally
        {
            ArrayPool<float>.Shared.Return(covariancePool);
        }
    }

    public static void ComputeRegularizedCovariance(
        ReadOnlySpan<float> residuals,
        int sampleCount,
        int dimension,
        float shrinkageLambda,
        Span<float> regularizedCovarianceOut,
        int degreesOfFreedom = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dimension);
        if (!float.IsFinite(shrinkageLambda) || (shrinkageLambda <= 0f) || (shrinkageLambda >= 1f))
        {
            throw new ArgumentOutOfRangeException(nameof(shrinkageLambda), "Shrinkage lambda must be in (0, 1).");
        }

        var matrixFloats = checked(dimension * dimension);
        var covSlice = regularizedCovarianceOut.Slice(0, matrixFloats);
        covSlice.Clear();

        for (var s = 0; s < sampleCount; s++)
        {
            var r = residuals.Slice(s * dimension, dimension);
            for (var i = 0; i < dimension; i++)
            {
                var ri = r[i];
                var rowSlice = covSlice.Slice(i * dimension, i + 1);
                var rPrefix = r.Slice(0, i + 1);
                AccumulateScaledRow(rPrefix, ri, rowSlice);
            }
        }

        // [Math Why]: Bessel's correction divides by effective degrees of freedom (N - K_prototypes) when centroids are estimated from samples.
        var effectiveDof = degreesOfFreedom > 0 ? degreesOfFreedom : sampleCount;
        var invSampleCount = 1f / effectiveDof;
        var trace = 0f;
        for (var i = 0; i < dimension; i++)
        {
            var diagIdx = (i * dimension) + i;
            covSlice[diagIdx] *= invSampleCount;
            trace += covSlice[diagIdx];
        }

        // [Math Why]: Ledoit-Wolf shrinkage target mu_target = Tr(Sigma_tied) / D; falls back to MinIsotropicDiagonalVariance if trace is zero.
        var meanVariance = MathF.Max(trace / dimension, MinIsotropicDiagonalVariance);
        var oneMinusLambda = 1f - shrinkageLambda;
        var offDiagScale = oneMinusLambda * invSampleCount;
        var diagShrinkOffset = shrinkageLambda * meanVariance;

        for (var i = 0; i < dimension; i++)
        {
            var rowOffset = i * dimension;
            for (var j = 0; j < i; j++)
            {
                var val = covSlice[rowOffset + j] * offDiagScale;
                covSlice[rowOffset + j] = val;
                covSlice[(j * dimension) + i] = val;
            }

            covSlice[rowOffset + i] = (covSlice[rowOffset + i] * oneMinusLambda) + diagShrinkOffset;
        }
    }

    public static void DecomposeCholesky(
        ReadOnlySpan<float> symmetricPositiveDefiniteMatrix,
        int dimension,
        Span<float> lowerTriangularOut)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dimension);
        var matrixFloats = checked(dimension * dimension);
        if (symmetricPositiveDefiniteMatrix.Length < matrixFloats || lowerTriangularOut.Length < matrixFloats)
        {
            throw new ArgumentException("Matrix spans must have length >= dimension * dimension.");
        }

        var lower = lowerTriangularOut.Slice(0, matrixFloats);
        lower.Clear();

        for (var i = 0; i < dimension; i++)
        {
            var rowI = i * dimension;
            for (var j = 0; j <= i; j++)
            {
                var rowJ = j * dimension;
                var sum = symmetricPositiveDefiniteMatrix[rowI + j] - DotProduct(lower.Slice(rowI, j), lower.Slice(rowJ, j));

                if (i == j)
                {
                    if (sum <= MinCholeskyPivot)
                    {
                        throw new InvalidOperationException($"Matrix is not strictly positive-definite at diagonal index {i} (pivot = {sum}).");
                    }

                    lower[rowI + j] = MathF.Sqrt(sum);
                }
                else
                {
                    lower[rowI + j] = sum / lower[rowJ + j];
                }
            }
        }
    }

    public static void InvertLowerTriangular(
        ReadOnlySpan<float> lowerTriangular,
        int dimension,
        Span<float> lowerInverseOut)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dimension);
        var matrixFloats = checked(dimension * dimension);
        var inv = lowerInverseOut.Slice(0, matrixFloats);
        inv.Clear();

        for (var i = 0; i < dimension; i++)
        {
            var diag = lowerTriangular[(i * dimension) + i];
            if (diag <= MinCholeskyPivot)
            {
                throw new InvalidOperationException($"Lower-triangular diagonal element L[{i},{i}] must be positive.");
            }

            inv[(i * dimension) + i] = 1f / diag;
        }

        for (var i = 1; i < dimension; i++)
        {
            var invDiagI = inv[(i * dimension) + i];
            for (var j = 0; j < i; j++)
            {
                var sum = 0f;
                for (var k = j; k < i; k++)
                {
                    sum += lowerTriangular[(i * dimension) + k] * inv[(k * dimension) + j];
                }

                inv[(i * dimension) + j] = -sum * invDiagI;
            }
        }
    }

    /// <summary>
    /// Computes Mahalanobis distance <c>d_M(feature, prototype) = || L^-1 (feature - prototype) ||_2</c>
    /// in zero heap allocations using the caller-provided <paramref name="differenceScratch"/> buffer.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public float ComputeMahalanobisDistance(
        ReadOnlySpan<float> feature,
        ReadOnlySpan<float> prototype,
        ReadOnlySpan<float> choleskyInverseFactor,
        int dimension,
        Span<float> differenceScratch)
    {
        var diff = differenceScratch.Slice(0, dimension);
        SubtractVectors(feature.Slice(0, dimension), prototype.Slice(0, dimension), diff);

        var sqDistance = 0f;
        for (var i = 0; i < dimension; i++)
        {
            var invRow = choleskyInverseFactor.Slice(i * dimension, i + 1);
            var diffPrefix = diff.Slice(0, i + 1);
            var projected = DotProduct(invRow, diffPrefix);
            sqDistance = MathF.FusedMultiplyAdd(projected, projected, sqDistance);
        }

        return MathF.Sqrt(MathF.Max(0f, sqDistance));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SubtractVectors(ReadOnlySpan<float> a, ReadOnlySpan<float> b, Span<float> dst)
    {
        var count = a.Length;
        ref var aBase = ref MemoryMarshal.GetReference(a);
        ref var bBase = ref MemoryMarshal.GetReference(b);
        ref var dstBase = ref MemoryMarshal.GetReference(dst);

        var i = 0;
        if (Vector256.IsHardwareAccelerated && (count >= Vector256<float>.Count))
        {
            var vecCount = Vector256<float>.Count;
            var limit = count & ~(vecCount - 1);
            for (; i < limit; i += vecCount)
            {
                var vA = Vector256.LoadUnsafe(ref aBase, (nuint)i);
                var vB = Vector256.LoadUnsafe(ref bBase, (nuint)i);
                (vA - vB).StoreUnsafe(ref dstBase, (nuint)i);
            }
        }

        for (; i < count; i++)
        {
            Unsafe.Add(ref dstBase, i) = Unsafe.Add(ref aBase, i) - Unsafe.Add(ref bBase, i);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AccumulateScaledRow(ReadOnlySpan<float> row, float scale, Span<float> accumulator)
    {
        var count = row.Length;
        ref var rBase = ref MemoryMarshal.GetReference(row);
        ref var accBase = ref MemoryMarshal.GetReference(accumulator);

        var i = 0;
        if (Vector256.IsHardwareAccelerated && (count >= Vector256<float>.Count))
        {
            var vecCount = Vector256<float>.Count;
            var limit = count & ~(vecCount - 1);
            var vScale = Vector256.Create(scale);
            for (; i < limit; i += vecCount)
            {
                var vR = Vector256.LoadUnsafe(ref rBase, (nuint)i);
                var vAcc = Vector256.LoadUnsafe(ref accBase, (nuint)i);
                var vRes = Fma.IsSupported ? Fma.MultiplyAdd(vR, vScale, vAcc) : vAcc + (vR * vScale);
                vRes.StoreUnsafe(ref accBase, (nuint)i);
            }
        }

        for (; i < count; i++)
        {
            Unsafe.Add(ref accBase, i) += Unsafe.Add(ref rBase, i) * scale;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static float DotProduct(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var count = a.Length;
        ref var aBase = ref MemoryMarshal.GetReference(a);
        ref var bBase = ref MemoryMarshal.GetReference(b);

        var i = 0;
        var sum = 0f;
        if (Vector256.IsHardwareAccelerated && (count >= Vector256<float>.Count))
        {
            var vecCount = Vector256<float>.Count;
            var limit = count & ~(vecCount - 1);
            var vSum = Vector256<float>.Zero;
            for (; i < limit; i += vecCount)
            {
                var vA = Vector256.LoadUnsafe(ref aBase, (nuint)i);
                var vB = Vector256.LoadUnsafe(ref bBase, (nuint)i);
                vSum = Fma.IsSupported ? Fma.MultiplyAdd(vA, vB, vSum) : vSum + (vA * vB);
            }

            sum = Vector256.Sum(vSum);
        }

        for (; i < count; i++)
        {
            sum += Unsafe.Add(ref aBase, i) * Unsafe.Add(ref bBase, i);
        }

        return sum;
    }
}
