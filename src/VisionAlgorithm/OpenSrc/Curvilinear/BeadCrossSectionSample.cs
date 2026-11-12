using System.Numerics;
using System.Runtime.CompilerServices;
using VisionAlgorithm.OpenSrc.Common;
using VisionAlgorithm.OpenSrc.Metrology;

namespace VisionAlgorithm.OpenSrc.Curvilinear;

/// <summary>
/// Represents a single cross-sectional station measurement along a curvilinear path (sealant bead, wire bond, or weld seam).
/// </summary>
public readonly record struct BeadCrossSectionSample(
    int StationIndex,
    Vector2 NominalCenterWorld,
    bool IsPresent,
    Vector2 MeasuredCenterWorld,
    float MeasuredWidthPixels,
    float PeakContrast);
