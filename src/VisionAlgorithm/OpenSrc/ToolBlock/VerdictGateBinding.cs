// File: src/VisionAlgorithm/OpenSrc/ToolBlock/VerdictGateBinding.cs
namespace VisionAlgorithm.OpenSrc.ToolBlock;

/// <summary>
/// Associates an upstream boolean judgment terminal (<see cref="Handle"/>) with its originating <see cref="SourceNodeId"/>
/// so <see cref="FinalVerdictAggregatorNode"/> can report the exact first-failing node ID on an <c>NG</c> or <c>Error</c> verdict.
/// </summary>
public readonly record struct VerdictGateBinding(
    int SourceNodeId,
    string GateName,
    TerminalHandle<bool> Handle);
