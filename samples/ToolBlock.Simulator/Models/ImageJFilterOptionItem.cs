// File: samples/ToolBlock.Simulator/Models/ImageJFilterOptionItem.cs
using VisionAlgorithm.OpenSrc.ImageJ;

namespace ToolBlock.Simulator.Models;

/// <summary>
/// Represents an ImageJ <c>Process</c> menu filter/binary/background operation option for the UI selector.
/// </summary>
public sealed record ImageJFilterOptionItem(
    ImageJFilterKind FilterKind,
    string Category,
    string DisplayName,
    string Description)
{
    public string FullMenuTitle => $"[{Category}] {DisplayName}";
}
