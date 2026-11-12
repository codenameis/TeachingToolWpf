using System.Numerics;
using System.Runtime.CompilerServices;
using VisionAlgorithm.OpenSrc.Common;

namespace VisionAlgorithm.OpenSrc.Curvilinear;

/// <summary>
/// Represents a single subpixel centerline point extracted via Steger (1998) Hessian eigen-analysis.
/// </summary>
public readonly record struct RidgeCenterlinePoint(
    int GridX,
    int GridY,
    Vector2 SubpixelPosition,
    Vector2 NormalDirection,
    float SecondDerivativeStrength);
