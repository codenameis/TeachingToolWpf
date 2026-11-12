using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace VisionAlgorithm.OpenSrc.PatMax;

/// <summary>
/// Ignores boundary contrast reversal: absolute dot product |d_m . d_r|.
/// </summary>
public readonly struct IgnorePolarityPolicy : IPolarityPolicy
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float EvaluateScalar(float dotProduct)
        => MathF.Abs(dotProduct);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<float> EvaluateVector256(Vector256<float> dotProduct)
        => Vector256.Abs(dotProduct);
}
