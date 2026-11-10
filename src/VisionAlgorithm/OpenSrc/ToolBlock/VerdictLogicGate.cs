// File: src/VisionAlgorithm/OpenSrc/ToolBlock/VerdictLogicGate.cs
namespace VisionAlgorithm.OpenSrc.ToolBlock;

/// <summary>
/// Specifies the boolean aggregation gate applied by <see cref="FinalVerdictAggregatorNode"/>.
/// </summary>
public enum VerdictLogicGate
{
    AllAnd = 0,
    AnyOr = 1,
    CustomMask = 2
}
