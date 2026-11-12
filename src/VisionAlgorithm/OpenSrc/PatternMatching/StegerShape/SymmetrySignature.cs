using System.Collections.Immutable;

namespace VisionAlgorithm.OpenSrc.PatternMatching;

/// <summary>
/// Self-symmetry decomposition signature for a trained shape model, capturing per-feature asymmetry key flags,
/// dominant symmetric core center/diameter, and 4-cardinal context sector radii and occupancy masks.
/// </summary>
public readonly record struct SymmetrySignature(
    ImmutableArray<byte> Flags,
    ImagePoint Center,
    double CoreDiameter,
    ImagePoint SectorRadii,
    byte OccupiedMask,
    byte EmptyMask)
{
    public static SymmetrySignature Empty => default;
}
