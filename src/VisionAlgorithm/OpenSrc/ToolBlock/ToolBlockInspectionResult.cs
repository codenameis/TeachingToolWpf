// File: src/VisionAlgorithm/OpenSrc/ToolBlock/ToolBlockInspectionResult.cs
namespace VisionAlgorithm.OpenSrc.ToolBlock;

/// <summary>
/// Immutable final inspection result and motion alignment control data produced by <see cref="FinalVerdictAggregatorNode"/>.
/// Allocated on the stack with zero GC overhead.
/// </summary>
public readonly record struct ToolBlockInspectionResult(
    FinalDecision Decision,
    int FirstFailedNodeId,
    int EvaluatedGateCount,
    int PassedGateCount,
    double PrimaryMetricValue,
    double SecondaryMetricValue,
    double AlignmentDeltaX,
    double AlignmentDeltaY,
    double AlignmentDeltaThetaDeg,
    double ActuatorStrokeU,
    double ActuatorStrokeV,
    double ActuatorStrokeW,
    bool HasValidAlignment)
{
    /// <summary>Gets a value indicating whether the overall inspection passed (<c>Decision == FinalDecision.Ok</c>).</summary>
    public bool IsOk => Decision == FinalDecision.Ok;
}
