// File: src/VisionAlgorithm/OpenSrc/ToolBlock/Line2D.cs
using System.Runtime.CompilerServices;

namespace VisionAlgorithm.OpenSrc.ToolBlock;

/// <summary>
/// Represents a 2D line in Hesse normal form $x \cos\theta + y \sin\theta - \rho = 0$ with unit normal
/// $(\cos\theta, \sin\theta)$ and segment endpoints <see cref="P1"/> and <see cref="P2"/>.
/// </summary>
public readonly record struct Line2D(
    double CosTheta,
    double SinTheta,
    double Rho,
    Point2D P1,
    Point2D P2)
{
    // [Math Why]: Minimum Euclidean norm squared (1e-18) guards against degenerate zero-length line segments.
    private const double MinimumSegmentLengthSquared = 1e-18;

    private readonly bool _isInitialized;

    /// <summary>Gets a value indicating whether this line has a valid unit normal.</summary>
    public bool IsValid => _isInitialized && double.IsFinite(CosTheta) && double.IsFinite(SinTheta) && double.IsFinite(Rho);

    /// <summary>Creates a validated <see cref="Line2D"/> from Hesse normal parameters and segment endpoints.</summary>
    public static Line2D Create(double cosTheta, double sinTheta, double rho, Point2D p1, Point2D p2)
    {
        double norm = Math.Sqrt((cosTheta * cosTheta) + (sinTheta * sinTheta));
        if (!double.IsFinite(norm) || norm <= MinimumSegmentLengthSquared)
        {
            throw new ArgumentOutOfRangeException(nameof(cosTheta), "Line normal vector must have non-zero finite length.");
        }

        double invNorm = 1.0 / norm;
        return new Line2D(cosTheta * invNorm, sinTheta * invNorm, rho * invNorm, p1, p2, isInitialized: true);
    }

    /// <summary>Creates a validated <see cref="Line2D"/> passing through two distinct endpoints <paramref name="p1"/> and <paramref name="p2"/>.</summary>
    public static bool TryCreateFromEndpoints(Point2D p1, Point2D p2, out Line2D line)
    {
        double dx = p2.X - p1.X;
        double dy = p2.Y - p1.Y;
        double lenSq = (dx * dx) + (dy * dy);
        if (!double.IsFinite(lenSq) || lenSq <= MinimumSegmentLengthSquared)
        {
            line = default;
            return false;
        }

        double invLen = 1.0 / Math.Sqrt(lenSq);
        double cosTheta = -dy * invLen;
        double sinTheta = dx * invLen;
        double rho = (cosTheta * p1.X) + (sinTheta * p1.Y);
        line = new Line2D(cosTheta, sinTheta, rho, p1, p2, isInitialized: true);
        return true;
    }

    private Line2D(double cosTheta, double sinTheta, double rho, Point2D p1, Point2D p2, bool isInitialized)
        : this(cosTheta, sinTheta, rho, p1, p2)
    {
        _isInitialized = isInitialized;
    }

    /// <summary>Evaluates the signed orthogonal distance $d = x \cos\theta + y \sin\theta - \rho$ from <paramref name="point"/> to this line.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public double SignedDistance(Point2D point)
        => (point.X * CosTheta) + (point.Y * SinTheta) - Rho;

    /// <summary>Projects <paramref name="point"/> orthogonally onto this line to obtain the foot of the perpendicular.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Point2D ProjectOrthogonalFoot(Point2D point)
    {
        double signedDist = SignedDistance(point);
        return new Point2D(
            point.X - (signedDist * CosTheta),
            point.Y - (signedDist * SinTheta));
    }
}
