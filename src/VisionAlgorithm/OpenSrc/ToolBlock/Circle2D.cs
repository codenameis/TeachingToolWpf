// File: src/VisionAlgorithm/OpenSrc/ToolBlock/Circle2D.cs
namespace VisionAlgorithm.OpenSrc.ToolBlock;

/// <summary>
/// Represents a 2D circle defined by its sub-pixel <see cref="Center"/>, <see cref="Radius"/>, and <see cref="Circularity"/> score in $[0, 1]$.
/// </summary>
public readonly record struct Circle2D(
    Point2D Center,
    double Radius,
    double Circularity)
{
    private readonly bool _isInitialized;

    /// <summary>Gets the circle diameter ($2 \times \text{Radius}$).</summary>
    public double Diameter => Radius * 2.0;

    /// <summary>Gets a value indicating whether this circle was constructed with a positive finite radius.</summary>
    public bool IsValid => _isInitialized && Center.IsFinite && double.IsFinite(Radius) && Radius > 0.0;

    public static Circle2D Create(Point2D center, double radius, double circularity)
    {
        if (!center.IsFinite)
        {
            throw new ArgumentOutOfRangeException(nameof(center), "Circle center coordinates must be finite.");
        }

        if (!double.IsFinite(radius) || radius <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(radius), "Circle radius must be finite and positive.");
        }

        if (!double.IsFinite(circularity) || circularity < 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(circularity), "Circularity must be finite and non-negative.");
        }

        return new Circle2D(center, radius, circularity, isInitialized: true);
    }

    private Circle2D(Point2D center, double radius, double circularity, bool isInitialized)
        : this(center, radius, circularity)
    {
        _isInitialized = isInitialized;
    }
}
