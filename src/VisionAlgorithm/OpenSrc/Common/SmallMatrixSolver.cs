// File: src/VisionAlgorithm/OpenSrc/Common/SmallMatrixSolver.cs
namespace VisionAlgorithm.OpenSrc.Common;

/// <summary>
/// Shared dense small-matrix linear algebra solvers (Gaussian elimination with partial pivoting and cyclic Jacobi
/// symmetric eigenvalue/SVD decomposition) used across geometric fitting and camera calibration.
/// </summary>
public static class SmallMatrixSolver
{
    // [Math Why]: Singular pivot threshold guards against division by zero on rank-deficient systems.
    private const double SingularPivotEpsilon = 1e-13;

    // [Perf Why]: 60 cyclic Jacobi sweeps converge a 9x9 symmetric matrix to machine precision (<1e-15).
    private const int JacobiMaxSweeps = 60;

    // [Math Why]: Off-diagonal rotation threshold below which Jacobi Givens rotations are skipped.
    private const double JacobiOffDiagonalEpsilon = 1e-15;

    /// <summary>
    /// Solves the $n \times n$ linear system $A x = b$ in-place using Gaussian elimination with partial pivoting.
    /// Returns <see langword="false"/> if $A$ is singular.
    /// </summary>
    public static bool TrySolveLinearSystem(Span<double> matrixA, Span<double> rhsB, int n, Span<double> solutionOut)
    {
        for (int col = 0; col < n; col++)
        {
            int pivotRow = col;
            double maxAbs = Math.Abs(matrixA[(col * n) + col]);
            for (int row = col + 1; row < n; row++)
            {
                double v = Math.Abs(matrixA[(row * n) + col]);
                if (v > maxAbs)
                {
                    maxAbs = v;
                    pivotRow = row;
                }
            }

            if (maxAbs <= SingularPivotEpsilon)
            {
                return false;
            }

            if (pivotRow != col)
            {
                for (int k = col; k < n; k++)
                {
                    (matrixA[(col * n) + k], matrixA[(pivotRow * n) + k]) =
                        (matrixA[(pivotRow * n) + k], matrixA[(col * n) + k]);
                }

                (rhsB[col], rhsB[pivotRow]) = (rhsB[pivotRow], rhsB[col]);
            }

            double pivotVal = matrixA[(col * n) + col];
            for (int row = col + 1; row < n; row++)
            {
                double factor = matrixA[(row * n) + col] / pivotVal;
                matrixA[(row * n) + col] = 0.0;
                for (int k = col + 1; k < n; k++)
                {
                    matrixA[(row * n) + k] -= factor * matrixA[(col * n) + k];
                }

                rhsB[row] -= factor * rhsB[col];
            }
        }

        for (int row = n - 1; row >= 0; row--)
        {
            double sum = rhsB[row];
            for (int k = row + 1; k < n; k++)
            {
                sum -= matrixA[(row * n) + k] * solutionOut[k];
            }

            solutionOut[row] = sum / matrixA[(row * n) + row];
        }

        return true;
    }

    /// <summary>
    /// Computes the eigenvector corresponding to the smallest eigenvalue of an $n \times n$ symmetric positive
    /// semi-definite matrix $S = A^T A$ via cyclic Jacobi Givens rotations.
    /// </summary>
    public static void FindSmallestSymmetricEigenvector(Span<double> symmetricMatrix, int n, Span<double> smallestEigenvectorOut)
    {
        Span<double> vMat = stackalloc double[n * n];
        for (int i = 0; i < n; i++)
        {
            vMat[(i * n) + i] = 1.0;
        }

        for (int sweep = 0; sweep < JacobiMaxSweeps; sweep++)
        {
            double maxOffDiag = 0.0;
            for (int p = 0; p < n - 1; p++)
            {
                for (int q = p + 1; q < n; q++)
                {
                    double apq = symmetricMatrix[(p * n) + q];
                    double absApq = Math.Abs(apq);
                    if (absApq > maxOffDiag)
                    {
                        maxOffDiag = absApq;
                    }

                    if (absApq <= JacobiOffDiagonalEpsilon)
                    {
                        continue;
                    }

                    double app = symmetricMatrix[(p * n) + p];
                    double aqq = symmetricMatrix[(q * n) + q];
                    double tau = (aqq - app) / (2.0 * apq);
                    double t = tau >= 0.0
                        ? 1.0 / (tau + Math.Sqrt(1.0 + (tau * tau)))
                        : -1.0 / (-tau + Math.Sqrt(1.0 + (tau * tau)));
                    double c = 1.0 / Math.Sqrt(1.0 + (t * t));
                    double s = t * c;

                    symmetricMatrix[(p * n) + p] = app - (t * apq);
                    symmetricMatrix[(q * n) + q] = aqq + (t * apq);
                    symmetricMatrix[(p * n) + q] = 0.0;
                    symmetricMatrix[(q * n) + p] = 0.0;

                    for (int k = 0; k < n; k++)
                    {
                        if (k != p && k != q)
                        {
                            double akp = symmetricMatrix[(k * n) + p];
                            double akq = symmetricMatrix[(k * n) + q];
                            double newKip = (c * akp) - (s * akq);
                            double newKiq = (s * akp) + (c * akq);
                            symmetricMatrix[(k * n) + p] = newKip;
                            symmetricMatrix[(p * n) + k] = newKip;
                            symmetricMatrix[(k * n) + q] = newKiq;
                            symmetricMatrix[(q * n) + k] = newKiq;
                        }

                        double vkp = vMat[(k * n) + p];
                        double vkq = vMat[(k * n) + q];
                        vMat[(k * n) + p] = (c * vkp) - (s * vkq);
                        vMat[(k * n) + q] = (s * vkp) + (c * vkq);
                    }
                }
            }

            if (maxOffDiag <= JacobiOffDiagonalEpsilon)
            {
                break;
            }
        }

        int minCol = 0;
        double minEigenvalue = symmetricMatrix[0];
        for (int i = 1; i < n; i++)
        {
            double eig = symmetricMatrix[(i * n) + i];
            if (eig < minEigenvalue)
            {
                minEigenvalue = eig;
                minCol = i;
            }
        }

        for (int i = 0; i < n; i++)
        {
            smallestEigenvectorOut[i] = vMat[(i * n) + minCol];
        }
    }
}
