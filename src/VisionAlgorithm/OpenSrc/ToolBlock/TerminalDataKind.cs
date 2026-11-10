// File: src/VisionAlgorithm/OpenSrc/ToolBlock/TerminalDataKind.cs
namespace VisionAlgorithm.OpenSrc.ToolBlock;

/// <summary>
/// Specifies the strongly-typed primitive storage bank used by a <see cref="ToolBlockContext"/> terminal slot.
/// </summary>
public enum TerminalDataKind
{
    None = 0,
    Pose = 1,
    Point = 2,
    Line = 3,
    Circle = 4,
    Scalar = 5,
    Boolean = 6
}
