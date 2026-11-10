// File: src/VisionAlgorithm/OpenSrc/ToolBlock/TerminalDirection.cs
namespace VisionAlgorithm.OpenSrc.ToolBlock;

/// <summary>
/// Specifies whether a <see cref="ToolBlockTerminalDescriptor"/> represents an input consumer or an output producer port.
/// </summary>
public enum TerminalDirection
{
    Input = 0,
    Output = 1
}
