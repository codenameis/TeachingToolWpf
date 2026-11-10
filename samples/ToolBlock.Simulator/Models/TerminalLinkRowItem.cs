namespace ToolBlock.Simulator.Models;

/// <summary>
/// Represents an active Terminal Link (<c>SourceNode.OutPort -&gt; TargetNode.InPort</c>) with its unboxed
/// <c>TerminalHandle&lt;T&gt;(SlotIndex)</c> and live runtime value in the Cognex-style Data Linking table.
/// </summary>
public sealed record TerminalLinkRowItem(
    int LinkIndex,
    int SourceNodeId,
    string SourceNodeName,
    string SourcePortName,
    int TargetNodeId,
    string TargetNodeName,
    string TargetPortName,
    string DataKindBadge,
    string SlotHandleBadge,
    string LiveValueSummary,
    bool IsValid,
    string AccentHex);
