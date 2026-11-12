using System.Numerics;
using System.Runtime.CompilerServices;
using VisionAlgorithm.OpenSrc.Common;

namespace VisionAlgorithm.OpenSrc.BlobRle;

/// <summary>
/// Geometric moments and shape descriptors of a connected component computed directly from its RLE chords.
/// </summary>
public readonly record struct BlobComponentFeature(
    int ComponentId,
    long AreaPixels,
    Vector2 Centroid,
    float PrincipalAxisAngleRad,
    float Eccentricity,
    int MinCol,
    int MinRow,
    int MaxCol,
    int MaxRow)
{
    public int BoundingWidth => (MaxCol - MinCol) + 1;
    public int BoundingHeight => (MaxRow - MinRow) + 1;
}
