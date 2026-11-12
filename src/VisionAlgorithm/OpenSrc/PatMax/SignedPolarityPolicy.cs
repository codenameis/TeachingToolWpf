using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace VisionAlgorithm.OpenSrc.PatMax;

/// <summary>
/// Respects dark-to-light vs light-to-dark boundary polarity: clamped positive dot product max(0, d_m . d_r).
/// </summary>
public readonly struct SignedPolarityPolicy : IPolarityPolicy
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float EvaluateScalar(float dotProduct)
        => MathF.Max(0.0f, dotProduct);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<float> EvaluateVector256(Vector256<float> dotProduct)
        => Avx.Max(Vector256<float>.Zero, dotProduct);
}
