// File: samples/ToolBlock.Simulator/Models/ProfileBarDisplayItem.cs
namespace ToolBlock.Simulator.Models;

/// <summary>
/// Represents a single spatial column in the ImageJ-style 1D Plot Profile and 1st-derivative Edge Gradient chart.
/// </summary>
public sealed record ProfileBarDisplayItem(
    int SampleIndex,
    string PositionLabel,
    float MeanIntensity,
    float EdgeGradient,
    double IntensityHeightPx,
    double GradientHeightPx,
    string IntensityColorHex,
    string GradientColorHex,
    string TooltipText);
