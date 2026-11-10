// File: samples/ToolBlock.Simulator/Models/AlgorithmResultSummaryRow.cs
namespace ToolBlock.Simulator.Models;

/// <summary>
/// Standardized result row displayed in the ImageJ + ToolBlock Simulator results table,
/// backed by <see cref="VisionAlgorithm.OpenSrc.Common.VisionSpcRecord"/> and <see cref="VisionAlgorithm.OpenSrc.Common.VisionReplayPayload"/>.
/// </summary>
public sealed record AlgorithmResultSummaryRow(
    int NodeId,
    string NodeName,
    string CategoryBadge,
    string StatusBadge,
    string VerdictBadge,
    string VerdictHex,
    string MatchedModelText,
    string WorldPoseText,
    string PrimaryMetricText,
    string AuxDiagnosticsText);
