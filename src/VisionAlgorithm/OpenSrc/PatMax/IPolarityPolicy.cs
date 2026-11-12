using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace VisionAlgorithm.OpenSrc.PatMax;

/// <summary>
/// Compile-time static polymorphism contract for signed vs polarity-ignoring gradient dot-product similarity (US 7,016,539).
/// </summary>
public interface IPolarityPolicy
{
    static abstract float EvaluateScalar(float dotProduct);
    static abstract Vector256<float> EvaluateVector256(Vector256<float> dotProduct);
}
