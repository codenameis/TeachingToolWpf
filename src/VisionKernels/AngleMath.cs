using System.Runtime.CompilerServices;

namespace VisionKernels;

/// <summary>
/// Bit-exact angular wrap and shortest circular distance helpers in degrees and radians.
/// </summary>
public static class AngleMath
{
    private const double DegreesShiftToPositive = 540.0;
    private const double FullCircleDegrees = 360.0;
    private const double HalfCircleDegrees = 180.0;
    private const double TwoPiRadians = 2.0 * Math.PI;
    private const float TwoPiRadiansSingle = 2.0f * MathF.PI;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double WrapDegrees180(double angleDegrees)
        => ((angleDegrees + DegreesShiftToPositive) % FullCircleDegrees) - HalfCircleDegrees;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double DistanceDegrees(double leftDegrees, double rightDegrees)
        => Math.Abs(WrapDegrees180(leftDegrees - rightDegrees));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double DistanceRadians(double leftRadians, double rightRadians)
    {
        var diff = Math.Abs(leftRadians - rightRadians) % TwoPiRadians;
        return diff > Math.PI ? TwoPiRadians - diff : diff;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float WrapRadiansPi(float angleRadians)
    {
        var wrapped = (angleRadians + MathF.PI) % TwoPiRadiansSingle;
        if (wrapped < 0.0f)
        {
            wrapped += TwoPiRadiansSingle;
        }

        return wrapped - MathF.PI;
    }
}
