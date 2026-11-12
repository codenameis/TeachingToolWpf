namespace VisionAlgorithm.OpenSrc.PatternMatching;

/// <summary>
/// Cohesive spatial, bounding-box, quadrant-coverage, and radiality statistics for a single connected contour component.
/// Replaces parallel per-root dictionaries during self-symmetry analysis.
/// </summary>
internal sealed class ComponentStatistics
{
    public int RootIndex { get; }
    public int Size { get; private set; }
    public double MinX { get; private set; }
    public double MaxX { get; private set; }
    public double MinY { get; private set; }
    public double MaxY { get; private set; }
    public double SumX { get; private set; }
    public double SumY { get; private set; }
    public int QuadrantMask { get; private set; }
    public double RadialSum { get; private set; }

    public double BoxCenterX => 0.5 * (MinX + MaxX);
    public double BoxCenterY => 0.5 * (MinY + MaxY);
    public double MeanX => SumX / Size;
    public double MeanY => SumY / Size;
    public double SpanX => Math.Max(1.0, MaxX - MinX);
    public double SpanY => Math.Max(1.0, MaxY - MinY);

    public ComponentStatistics(int rootIndex, double initialX, double initialY)
    {
        RootIndex = rootIndex;
        Size = 1;
        MinX = initialX;
        MaxX = initialX;
        MinY = initialY;
        MaxY = initialY;
    }

    public void IncludeBoundsPoint(double x, double y)
    {
        Size++;
        if (x < MinX)
        {
            MinX = x;
        }

        if (x > MaxX)
        {
            MaxX = x;
        }

        if (y < MinY)
        {
            MinY = y;
        }

        if (y > MaxY)
        {
            MaxY = y;
        }
    }

    public void AccumulateMomentsAndQuadrants(ModelFeature feature)
    {
        // [Math Why]: Keep identical summation and quadrant bit-mask arithmetic order relative to finalized bounding-box center.
        SumX += feature.X;
        SumY += feature.Y;
        var cx = BoxCenterX;
        var cy = BoxCenterY;
        var quadrant = (feature.X >= cx ? 1 : 0) + (feature.Y >= cy ? 2 : 0);
        QuadrantMask |= 1 << quadrant;

        var cdx = feature.X - cx;
        var cdy = feature.Y - cy;
        var cdr = Math.Sqrt((cdx * cdx) + (cdy * cdy));
        if (cdr > 1e-4)
        {
            RadialSum += Math.Abs(((cdx * feature.NormalX) + (cdy * feature.NormalY)) / cdr);
        }
    }
}
