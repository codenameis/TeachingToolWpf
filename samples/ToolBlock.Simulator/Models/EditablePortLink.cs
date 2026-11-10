using VisionAlgorithm.OpenSrc.ToolBlock;

namespace ToolBlock.Simulator.Models;

/// <summary>
/// Represents a user-configured port connection between an upstream node's output terminal and a downstream node's input terminal.
/// </summary>
public sealed record EditablePortLink(
    int SourceNodeId,
    string SourceOutputPort,
    int TargetNodeId,
    string TargetInputPort,
    TerminalDataKind DataKind);
