// File: src/VisionAlgorithm/OpenSrc/ToolBlock/FinalDecision.cs
namespace VisionAlgorithm.OpenSrc.ToolBlock;

/// <summary>
/// Represents the final three-state quality verdict of a <see cref="ToolBlockRunner"/> inspection cycle.
/// </summary>
public enum FinalDecision
{
    Ok = 0,
    Ng = 1,
    Error = 2
}
