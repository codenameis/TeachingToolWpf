// File: src/VisionAlgorithm/OpenSrc/ToolBlock/CaliperMetroMode.cs
namespace VisionAlgorithm.OpenSrc.ToolBlock;

/// <summary>
/// Specifies the metrology sub-pixel measurement mode executed by <see cref="CaliperLineCircleToolNode"/>.
/// </summary>
public enum CaliperMetroMode
{
    SingleEdge = 0,
    EdgePairWidth = 1,
    RakeLineFit = 2,
    SpokeCircleFit = 3
}
