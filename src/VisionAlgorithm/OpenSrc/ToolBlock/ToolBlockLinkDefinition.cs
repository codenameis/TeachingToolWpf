// File: src/VisionAlgorithm/OpenSrc/ToolBlock/ToolBlockLinkDefinition.cs
namespace VisionAlgorithm.OpenSrc.ToolBlock;

/// <summary>
/// Cold-path definition of a directed data link from an upstream node's output port to a downstream node's input port.
/// </summary>
public readonly record struct ToolBlockLinkDefinition(
    int SourceNodeId,
    string SourceOutputPort,
    int TargetNodeId,
    string TargetInputPort,
    TerminalDataKind ExpectedDataKind,
    int SharedSlotIndex);
