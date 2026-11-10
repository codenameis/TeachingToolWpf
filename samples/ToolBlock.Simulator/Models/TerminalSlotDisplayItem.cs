// File: samples/ToolBlock.Simulator/Models/TerminalSlotDisplayItem.cs
namespace ToolBlock.Simulator.Models;

/// <summary>
/// Immutable UI row model representing an allocated <c>ToolBlockContext</c> terminal slot, its producer/consumer link,
/// and its runtime value and validity status.
/// </summary>
public sealed record TerminalSlotDisplayItem(
    string SlotHandleLabel,
    string DataKind,
    string ProducerPort,
    string ConsumerPorts,
    string FormattedValue,
    bool IsValid,
    string StatusBadge);
