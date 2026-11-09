// File: src/VisionAlgorithm/OpenSrc/BlobRle/PixelRun.cs
namespace VisionAlgorithm.OpenSrc.BlobRle;

/// <summary>
/// Represents a contiguous horizontal foreground interval $[StartCol, EndCol]$ (inclusive) on row <paramref name="Row"/>.
/// Operations on run-length intervals scale with chord count $O(N_{\text{runs}})$ rather than pixel count $O(W \times H)$.
/// </summary>
public readonly record struct PixelRun(
    int Row,
    int StartCol,
    int EndCol,
    int Label = 0)
{
    /// <summary>Gets the number of foreground pixels covered by this horizontal chord: $EndCol - StartCol + 1$.</summary>
    public int Length => (EndCol - StartCol) + 1;

    /// <summary>Determines whether this horizontal chord overlaps or touches <paramref name="other"/> under 8-connectivity.</summary>
    public bool Overlaps8Connected(PixelRun other)
        => StartCol <= (other.EndCol + 1) && other.StartCol <= (EndCol + 1);

    /// <summary>Determines whether this horizontal chord overlaps <paramref name="other"/> under 4-connectivity.</summary>
    public bool Overlaps4Connected(PixelRun other)
        => StartCol <= other.EndCol && other.StartCol <= EndCol;
}
