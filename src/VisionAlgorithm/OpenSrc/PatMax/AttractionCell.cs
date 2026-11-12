using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace VisionAlgorithm.OpenSrc.PatMax;

/// <summary>
/// Precomputed vector attraction cell storing displacement to the nearest boundary dipole and its local normal (US 7,065,262).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct AttractionCell(
    float DeltaX,
    float DeltaY,
    float NormalX,
    float NormalY,
    float Distance,
    int NearestDipoleIndex)
{
    public const int UnassignedIndex = -1;

    public bool HasNearestDipole => NearestDipoleIndex >= 0;

    public static AttractionCell Unassigned => new(0f, 0f, 0f, 0f, float.PositiveInfinity, UnassignedIndex);
}
