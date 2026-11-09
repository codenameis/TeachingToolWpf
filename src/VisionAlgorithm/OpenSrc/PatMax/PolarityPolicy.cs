// File: src/VisionAlgorithm/OpenSrc/PatMax/PolarityPolicy.cs
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
