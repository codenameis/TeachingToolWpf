// File: samples/ToolBlock.Simulator/Models/DynamicToolBlockSnapshot.cs
using System.Collections.Generic;
using VisionAlgorithm.OpenSrc.Common;
using VisionAlgorithm.OpenSrc.ToolBlock;
using VisionCanvas.Rendering;

namespace ToolBlock.Simulator.Models;

/// <summary>
/// Immutable execution snapshot produced by compiling and running the proprietary ImageJ + ToolBlock DAG
/// against the active <c>VisionCanvas</c> document and image.
/// Includes unified <see cref="VisionInspectionEnvelope"/> records for SPC analytics and 100% overlay replay.
/// </summary>
public sealed record DynamicToolBlockSnapshot(
    ToolBlockCompileStatus CompileStatus,
    string CompileMessage,
    ToolBlockInspectionResult VerdictResult,
    double ExecutionMicroseconds,
    ResultOverlay CanvasOverlay,
    IReadOnlyList<DagNodeDisplayItem> OrderedDagNodes,
    IReadOnlyList<TerminalLinkRowItem> LinkRows,
    IReadOnlyList<TerminalSlotDisplayItem> SlotItems,
    IReadOnlyList<TerminalPortOption> AvailableOutputPorts,
    IReadOnlyList<TerminalPortOption> AvailableInputPorts,
    IReadOnlyList<AlgorithmResultSummaryRow> ResultSummaryRows,
    IReadOnlyList<VisionInspectionEnvelope> InspectionEnvelopes,
    double StrokeU1Mm,
    double StrokeU2Mm,
    double StrokeVMm,
    double StrokeWMm);
