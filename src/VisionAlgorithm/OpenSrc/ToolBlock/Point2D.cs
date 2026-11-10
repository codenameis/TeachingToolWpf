// File: src/VisionAlgorithm/OpenSrc/ToolBlock/Point2D.cs
using System.Numerics;
using System.Runtime.CompilerServices;

namespace VisionAlgorithm.OpenSrc.ToolBlock;

/// <summary>
/// Represents an immutable 2D sub-pixel or calibrated physical coordinate $(X, Y)$ in double precision.
/// </summary>
public readonly record struct Point2D(double X, double Y)
{
    /// <summary>Gets the origin point $(0, 0)$.</summary>
    public static Point2D Zero => new(0.0, 0.0);

    /// <summary>Gets a value indicating whether both coordinates are finite real numbers.</summary>
    public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y);

    /// <summary>Converts a single-precision <see cref="Vector2"/> into a double-precision <see cref="Point2D"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Point2D FromVector2(Vector2 vector)
        => new(vector.X, vector.Y);

    /// <summary>Converts this <see cref="Point2D"/> into a single-precision <see cref="Vector2"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Vector2 ToVector2()
        => new((float)X, (float)Y);

    /// <summary>Computes the Euclidean distance to <paramref name="other"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public double DistanceTo(Point2D other)
    {
        double dx = other.X - X;
        double dy = other.Y - Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }
}
