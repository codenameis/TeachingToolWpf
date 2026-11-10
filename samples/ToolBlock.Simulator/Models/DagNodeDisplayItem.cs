// File: samples/ToolBlock.Simulator/Models/DagNodeDisplayItem.cs
using System;

namespace ToolBlock.Simulator.Models;

/// <summary>
/// Display model for a single algorithm or combiner node in the ToolBlock DAG execution tree.
/// </summary>
public sealed record DagNodeDisplayItem(
    int ExecutionStep,
    int NodeId,
    string Name,
    VisionToolKind Kind,
    string ToolTypeBadge,
    Guid? BoundRoiId,
    string BoundRoiLabel,
    string InPortSummary,
    string OutPortSummary,
    string LiveMetricSummary,
    string StatusBadge,
    string StatusHex);
