// File: samples/ToolBlock.Simulator/Models/ToolboxPaletteItem.cs
namespace ToolBlock.Simulator.Models;

/// <summary>
/// Represents an available proprietary vision algorithm or combiner in the ImageJ + ToolBlock Simulator palette.
/// </summary>
public sealed record ToolboxPaletteItem(
    VisionToolKind Kind,
    string DisplayName,
    string ShortCode,
    string Category,
    string AccentHex,
    string ScopeBadge,
    string Description);
