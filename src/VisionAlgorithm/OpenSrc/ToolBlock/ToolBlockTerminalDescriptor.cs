// File: src/VisionAlgorithm/OpenSrc/ToolBlock/ToolBlockTerminalDescriptor.cs
namespace VisionAlgorithm.OpenSrc.ToolBlock;

/// <summary>
/// Cold-path metadata describing a named input or output terminal port on an <see cref="IToolBlockNode"/>
/// and its bound integer slot index in <see cref="ToolBlockContext"/>.
/// </summary>
public readonly record struct ToolBlockTerminalDescriptor(
    string PortName,
    TerminalDirection Direction,
    TerminalDataKind DataKind,
    int SlotIndex)
{
    /// <summary>Gets a value indicating whether this port is bound to a valid slot index (<c>SlotIndex &gt;= 0</c>).</summary>
    public bool IsBound => SlotIndex >= 0;
}
