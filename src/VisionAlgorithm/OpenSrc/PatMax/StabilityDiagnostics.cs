// File: src/VisionAlgorithm/OpenSrc/PatMax/StabilityDiagnostics.cs
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace VisionAlgorithm.OpenSrc.PatMax;

/// <summary>
/// Result of the pattern boundary orientation stability analysis (US 6,993,177).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct StabilityReport(
    float SumCosSquared,
    float SumCosSin,
    float SumSinSquared,
    float MajorEigenvalue,
    float MinorEigenvalue,
    float ConditionNumber,
    bool IsStable,
    bool HasInsufficientFeatures,
    bool HasApertureWarning)
{
    public static StabilityReport Empty => new(
        0f,
        0f,
        0f,
        0f,
        0f,
        float.PositiveInfinity,
        IsStable: false,
        HasInsufficientFeatures: true,
        HasApertureWarning: true);
}

/// <summary>
/// Evaluates the 2x2 weighted gradient normal covariance matrix and condition number for translation/orientation stability (US 6,993,177).
/// </summary>
public static class StabilityMatrixAnalyzer
{
    // [Math Why]: Prevents division-by-zero when all boundary normals are strictly collinear (rank-1 matrix).
    private const float SingularEigenvalueEpsilon = 1e-6f;

    public static StabilityReport Evaluate(EdgeDipoleSoA dipoles, in PatMaxTrainParams trainParams)
    {
        ArgumentNullException.ThrowIfNull(dipoles);
        if (trainParams.IsEmpty)
        {
            throw new ArgumentException("PatMaxTrainParams must not be empty.", nameof(trainParams));
        }

        var count = dipoles.Count;
        if (count < trainParams.MinFeatureCount)
        {
            return StabilityReport.Empty;
        }

        AccumulateWeightedCovariance(
            dipoles.CosTheta,
            dipoles.SinTheta,
            dipoles.Magnitude,
            out var mxx,
            out var mxy,
            out var myy);

        // [Math Why]: Analytic eigenvalues of symmetric 2x2 matrix [mxx, mxy; mxy, myy]: lambda = 0.5 * (trace +/- sqrt((mxx - myy)^2 + 4 * mxy^2)).
        var trace = mxx + myy;
        var diff = mxx - myy;
        var fourMxySquared = (2f * mxy) * (2f * mxy);
        var discriminant = MathF.Sqrt((diff * diff) + fourMxySquared);

        var majorEigenvalue = 0.5f * (trace + discriminant);
        var minorEigenvalue = MathF.Max(0f, 0.5f * (trace - discriminant));

        float conditionNumber;
        bool hasApertureWarning;
        if (minorEigenvalue <= SingularEigenvalueEpsilon)
        {
            conditionNumber = float.PositiveInfinity;
            hasApertureWarning = true;
        }
        else
        {
            conditionNumber = MathF.Sqrt(majorEigenvalue / minorEigenvalue);
            hasApertureWarning = conditionNumber > trainParams.MaxConditionNumber;
        }

        return new StabilityReport(
            mxx,
            mxy,
            myy,
            majorEigenvalue,
            minorEigenvalue,
            conditionNumber,
            IsStable: !hasApertureWarning,
            HasInsufficientFeatures: false,
            HasApertureWarning: hasApertureWarning);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void AccumulateWeightedCovariance(
        ReadOnlySpan<float> cosSpan,
        ReadOnlySpan<float> sinSpan,
        ReadOnlySpan<float> magSpan,
        out float mxx,
        out float mxy,
        out float myy)
    {
        var length = cosSpan.Length;
        ref var cosRef = ref MemoryMarshal.GetReference(cosSpan);
        ref var sinRef = ref MemoryMarshal.GetReference(sinSpan);
        ref var magRef = ref MemoryMarshal.GetReference(magSpan);

        var index = 0;
        var sumXX = 0f;
        var sumXY = 0f;
        var sumYY = 0f;

        // [Perf Why]: Accumulate 8 weighted outer products per cycle in AVX2 registers using FMA.
        if (Avx2.IsSupported)
        {
            var simdLimit = length & ~(Vector256<float>.Count - 1);
            var vXX = Vector256<float>.Zero;
            var vXY = Vector256<float>.Zero;
            var vYY = Vector256<float>.Zero;

            for (; index < simdLimit; index += Vector256<float>.Count)
            {
                var c = Vector256.LoadUnsafe(ref Unsafe.Add(ref cosRef, index));
                var s = Vector256.LoadUnsafe(ref Unsafe.Add(ref sinRef, index));
                var w = Vector256.LoadUnsafe(ref Unsafe.Add(ref magRef, index));

                var wc = w * c;
                if (Fma.IsSupported)
                {
                    vXX = Fma.MultiplyAdd(wc, c, vXX);
                    vXY = Fma.MultiplyAdd(wc, s, vXY);
                    vYY = Fma.MultiplyAdd(w * s, s, vYY);
                }
                else
                {
                    vXX += wc * c;
                    vXY += wc * s;
                    vYY += (w * s) * s;
                }
            }

            sumXX = Vector256.Sum(vXX);
            sumXY = Vector256.Sum(vXY);
            sumYY = Vector256.Sum(vYY);
        }

        for (; index < length; index++)
        {
            var c = Unsafe.Add(ref cosRef, index);
            var s = Unsafe.Add(ref sinRef, index);
            var w = Unsafe.Add(ref magRef, index);
            var wc = w * c;
            sumXX += wc * c;
            sumXY += wc * s;
            sumYY += (w * s) * s;
        }

        mxx = sumXX;
        mxy = sumXY;
        myy = sumYY;
    }
}
