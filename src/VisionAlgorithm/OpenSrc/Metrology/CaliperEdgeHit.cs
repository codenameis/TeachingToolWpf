using System.Numerics;
using System.Runtime.CompilerServices;
using VisionAlgorithm.OpenSrc.Common;
using VisionKernels;

namespace VisionAlgorithm.OpenSrc.Metrology;

/// <summary>
/// Represents a single subpixel edge transition detected along a 1D caliper profile.
/// </summary>
public readonly record struct CaliperEdgeHit(
    float SubpixelPosition,
    Vector2 WorldPoint,
    float GradientAmplitude,
    CaliperEdgePolarity Polarity);
