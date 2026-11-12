using System.Numerics;
using System.Runtime.CompilerServices;
using VisionAlgorithm.OpenSrc.Common;
using VisionKernels;

namespace VisionAlgorithm.OpenSrc.Metrology;

/// <summary>
/// Represents the subpixel width and world-space midpoint of a measured edge pair.
/// </summary>
public readonly record struct SubpixelWidthMeasurement(
    float WidthPixels,
    Vector2 MidpointWorld);
