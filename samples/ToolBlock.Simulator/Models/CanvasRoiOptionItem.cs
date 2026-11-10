using System;

namespace ToolBlock.Simulator.Models;

/// <summary>
/// Represents a selectable <c>VisionCanvas</c> ROI item for binding to a ToolBlock ROI node.
/// </summary>
public sealed record CanvasRoiOptionItem(
    Guid RoiId,
    string Name,
    string GeometrySummary);
