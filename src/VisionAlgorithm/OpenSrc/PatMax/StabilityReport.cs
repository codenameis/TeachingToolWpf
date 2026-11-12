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
