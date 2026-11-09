// File: src/VisionAlgorithm/OpenSrc/Common/LedoitWolfMahalanobis.cs
using System.Runtime.CompilerServices;

namespace VisionAlgorithm.OpenSrc.Common;

/// <summary>
/// Implements the Oracle Approximating Shrinkage / Ledoit-Wolf analytical covariance estimator
/// (Ledoit &amp; Wolf, "A well-conditioned estimator for large-dimensional covariance matrices", Journal of Multivariate Analysis 88(2), 2004)
/// and lower-triangular Cholesky decomposition $L L^T = \Sigma_{\text{LW}}$ for exact Mahalanobis distance evaluation.
/// </summary>
public sealed class LedoitWolfMahalanobis
{
    // [Math Why]: Minimum shrinkage floor guarantees strict positive definiteness when N << D.
    private const float MinimumShrinkageIntensity = 0.02f;

    // [Math Why]: Diagonal pivot floor guards Cholesky factorization against numerical underflow.
    private const double MinimumCholeskyPivot = 1e-8;

    private readonly float[] _lowerCholesky;

    public int Dimension { get; }
    public float ShrinkageLambda { get; }
    public ReadOnlySpan<float> LowerCholeskyMatrix => _lowerCholesky;

    private LedoitWolfMahalanobis(int dimension, float shrinkageLambda, float[] lowerCholesky)
    {
        Dimension = dimension;
        ShrinkageLambda = shrinkageLambda;
        _lowerCholesky = lowerCholesky;
    }

    /// <summary>
    /// Fits a Ledoit-Wolf shrunk covariance matrix $\Sigma_{\text{LW}} = (1 - \lambda) S + \lambda \mu_{\text{trace}} I_D$
    /// from centered residual vectors $[N \times D]$ with $K$ class degrees of freedom consumed, and factors $L L^T = \Sigma_{\text{LW}}$.
    /// </summary>
    public static LedoitWolfMahalanobis FitFromCenteredResiduals(
        ReadOnlySpan<float> centeredResiduals,
        int sampleCount,
        int dimension,
        int classClusterCount = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sampleCount, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(dimension, 0);
        if (centeredResiduals.Length < checked(sampleCount * dimension))
        {
            throw new ArgumentException("centeredResiduals length must be >= sampleCount * dimension.", nameof(centeredResiduals));
        }

        double[] cov = new double[dimension * dimension];
        int degreesOfFreedom = Math.Max(1, sampleCount - Math.Max(1, classClusterCount));
        double invDof = 1.0 / degreesOfFreedom;
        double invN = 1.0 / sampleCount;

        for (int n = 0; n < sampleCount; n++)
        {
            ReadOnlySpan<float> r = centeredResiduals.Slice(n * dimension, dimension);
            for (int i = 0; i < dimension; i++)
            {
                double ri = r[i];
                int rowOffset = i * dimension;
                for (int j = 0; j <= i; j++)
                {
                    cov[rowOffset + j] += ri * r[j];
                }
            }
        }

        double traceS = 0.0;
        double frobeniusDiffSq = 0.0;
        for (int i = 0; i < dimension; i++)
        {
            int rowOffset = i * dimension;
            for (int j = 0; j <= i; j++)
            {
                cov[rowOffset + j] *= invDof;
                cov[(j * dimension) + i] = cov[rowOffset + j];
            }

            traceS += cov[rowOffset + i];
        }

        double muTrace = traceS / dimension;
        if (muTrace <= MinimumCholeskyPivot)
        {
            muTrace = 1.0 / dimension;
        }

        for (int i = 0; i < dimension; i++)
        {
            int rowOffset = i * dimension;
            for (int j = 0; j < dimension; j++)
            {
                double target = i == j ? muTrace : 0.0;
                double diff = cov[rowOffset + j] - target;
                frobeniusDiffSq += diff * diff;
            }
        }

        float lambda = 1.0f;
        if (sampleCount > 1 && frobeniusDiffSq > 1e-12)
        {
            double sumQuarticAndCross = 0.0;
            for (int n = 0; n < sampleCount; n++)
            {
                ReadOnlySpan<float> r = centeredResiduals.Slice(n * dimension, dimension);
                double normSq = 0.0;
                for (int i = 0; i < dimension; i++)
                {
                    normSq += (double)r[i] * r[i];
                }

                double quadForm = 0.0;
                for (int i = 0; i < dimension; i++)
                {
                    double rowDot = 0.0;
                    int rowOffset = i * dimension;
                    for (int j = 0; j < dimension; j++)
                    {
                        rowDot += cov[rowOffset + j] * r[j];
                    }

                    quadForm += r[i] * rowDot;
                }

                sumQuarticAndCross += (normSq * normSq) - quadForm;
            }

            double betaBar = Math.Max(0.0, sumQuarticAndCross * invN * invN);
            lambda = (float)Math.Clamp(betaBar / frobeniusDiffSq, MinimumShrinkageIntensity, 1.0);
        }

        double oneMinusLambda = 1.0 - lambda;
        double diagTarget = lambda * muTrace;
        for (int i = 0; i < dimension; i++)
        {
            int rowOffset = i * dimension;
            for (int j = 0; j <= i; j++)
            {
                cov[rowOffset + j] *= oneMinusLambda;
                if (i == j)
                {
                    cov[rowOffset + j] += diagTarget;
                }
            }
        }

        float[] lowerL = FactorCholeskyLower(cov, dimension);
        return new LedoitWolfMahalanobis(dimension, lambda, lowerL);
    }

    /// <summary>
    /// Computes the exact Mahalanobis distance $\sqrt{(x - \mu)^T \Sigma_{\text{LW}}^{-1} (x - \mu)} = \|L^{-1}(x - \mu)\|_2$
    /// via forward substitution on the lower Cholesky factor $L$.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float ComputeDistance(
        ReadOnlySpan<float> featureVector,
        ReadOnlySpan<float> prototypeMean,
        Span<float> scratchForwardBuffer)
    {
        int d = Dimension;
        if (featureVector.Length < d || prototypeMean.Length < d || scratchForwardBuffer.Length < d)
        {
            throw new ArgumentException("Buffers must have length >= Dimension.");
        }

        ReadOnlySpan<float> lMat = _lowerCholesky;
        double sumSq = 0.0;

        for (int i = 0; i < d; i++)
        {
            double acc = featureVector[i] - prototypeMean[i];
            int rowOffset = i * d;
            for (int j = 0; j < i; j++)
            {
                acc -= lMat[rowOffset + j] * scratchForwardBuffer[j];
            }

            float yi = (float)(acc / lMat[rowOffset + i]);
            scratchForwardBuffer[i] = yi;
            sumSq += (double)yi * yi;
        }

        return (float)Math.Sqrt(sumSq);
    }

    private static float[] FactorCholeskyLower(double[] symmetricMatrix, int d)
    {
        float[] lower = new float[d * d];
        for (int i = 0; i < d; i++)
        {
            int rowI = i * d;
            for (int j = 0; j <= i; j++)
            {
                int rowJ = j * d;
                double sum = symmetricMatrix[rowI + j];
                for (int k = 0; k < j; k++)
                {
                    sum -= (double)lower[rowI + k] * lower[rowJ + k];
                }

                if (i == j)
                {
                    lower[rowI + i] = (float)Math.Sqrt(Math.Max(MinimumCholeskyPivot, sum));
                }
                else
                {
                    lower[rowI + j] = (float)(sum / lower[rowJ + j]);
                }
            }
        }

        return lower;
    }
}
