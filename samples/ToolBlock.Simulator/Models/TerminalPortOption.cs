using VisionAlgorithm.OpenSrc.ToolBlock;

namespace ToolBlock.Simulator.Models;

/// <summary>
/// Represents a strongly-typed input or output terminal port exposed by a ToolBlock node
/// for interactive Cognex-style port linking.
/// </summary>
public sealed record TerminalPortOption(
    int NodeId,
    string NodeName,
    string PortName,
    TerminalDirection Direction,
    TerminalDataKind DataKind,
    string DisplayLabel);
