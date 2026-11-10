// File: src/VisionAlgorithm/OpenSrc/Metrology/CaliperSelectionMode.cs
namespace VisionAlgorithm.OpenSrc.Metrology;

/// <summary>
/// Specifies the edge or edge-pair candidate ranking rule along a 1D caliper scan profile
/// (Cognex <c>CogCaliperScoringFunction</c> / HALCON <c>measure_pos</c> <c>Select</c> parity).
/// </summary>
public enum CaliperSelectionMode
{
    /// <summary>
    /// Selects the candidate or pair maximizing raw gradient contrast amplitude.
    /// </summary>
    StrongestContrast = 0,

    /// <summary>
    /// Selects the earliest valid candidate or pair along the positive scan direction $u$.
    /// </summary>
    FirstEdge = 1,

    /// <summary>
    /// Selects the latest valid candidate or pair along the positive scan direction $u$.
    /// </summary>
    LastEdge = 2,

    /// <summary>
    /// Evaluates continuous multi-criteria Gaussian affinity combining normalized contrast,
    /// expected profile center position $u_0$, and expected pair width $w_0$.
    /// </summary>
    CompositeScore = 3
}
