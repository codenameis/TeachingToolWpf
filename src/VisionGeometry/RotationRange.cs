namespace VisionGeometry;

/// <summary>The connected interval of permitted rotation starting at zero, without wrapping angles.</summary>
public readonly record struct RotationRange
{
    public double MinimumDegrees
    {
        get;
    }

    public double MaximumDegrees
    {
        get;
    }

    public static RotationRange Unbounded => new(double.NegativeInfinity, double.PositiveInfinity);

    public RotationRange(double minimumDegrees, double maximumDegrees)
    {
        if (double.IsNaN(minimumDegrees) || minimumDegrees > 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumDegrees));
        }

        if (double.IsNaN(maximumDegrees) || maximumDegrees < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumDegrees));
        }

        MinimumDegrees = minimumDegrees;
        MaximumDegrees = maximumDegrees;
    }

    public double Clamp(double requestedDegrees)
    {
        if (!double.IsFinite(requestedDegrees))
        {
            throw new ArgumentOutOfRangeException(nameof(requestedDegrees));
        }

        return Math.Clamp(requestedDegrees, MinimumDegrees, MaximumDegrees);
    }
}
