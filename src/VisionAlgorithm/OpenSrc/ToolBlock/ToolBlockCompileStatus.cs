// File: src/VisionAlgorithm/OpenSrc/ToolBlock/ToolBlockCompileStatus.cs
namespace VisionAlgorithm.OpenSrc.ToolBlock;

/// <summary>
/// Specifies the validation outcome of compiling a <see cref="ToolBlockBuilder"/> into an executable <see cref="ToolBlockRunner"/>.
/// </summary>
public enum ToolBlockCompileStatus
{
    Success = 0,
    EmptyPipeline = 1,
    DuplicateNodeId = 2,
    UnknownNodeId = 3,
    UnknownPortName = 4,
    PortTypeMismatch = 5,
    CycleDetected = 6,
    SlotIndexOutOfRange = 7
}
