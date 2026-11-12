using System.Numerics;
using System.Runtime.CompilerServices;
using VisionAlgorithm.OpenSrc.Common;
using VisionKernels;

namespace VisionAlgorithm.OpenSrc.Metrology;

/// <summary>
/// Specifies the intensity transition polarity along the caliper scan direction.
/// </summary>
public enum CaliperEdgePolarity
{
    DarkToLight = 0,
    LightToDark = 1,
    Any = 2
}
