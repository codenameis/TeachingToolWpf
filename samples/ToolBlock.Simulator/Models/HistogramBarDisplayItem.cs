// File: samples/ToolBlock.Simulator/Models/HistogramBarDisplayItem.cs
namespace ToolBlock.Simulator.Models;

/// <summary>
/// Display item for a single bar in the ImageJ-style interactive ROI intensity histogram chart.
/// </summary>
public sealed record HistogramBarDisplayItem(
    int BinIndex,
    string RangeLabel,
    int PixelCount,
    double NormalizedHeightPx,
    string BarColorHex,
    string TooltipText);
