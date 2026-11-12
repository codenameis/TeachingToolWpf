using System.Numerics;
using System.Runtime.CompilerServices;
using VisionAlgorithm.OpenSrc.Common;

namespace VisionAlgorithm.OpenSrc.Curvilinear;

/// <summary>
/// Polarity of the curvilinear structure (bright ridge/bead vs. dark crack/scratch).
/// </summary>
public enum RidgePolarity
{
    BrightLine = 0,
    DarkLine = 1
}
