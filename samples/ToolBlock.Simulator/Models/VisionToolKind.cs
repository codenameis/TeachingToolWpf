// File: samples/ToolBlock.Simulator/Models/VisionToolKind.cs
namespace ToolBlock.Simulator.Models;

/// <summary>
/// Proprietary Vision ToolBlock algorithm &amp; virtual combiner categories available in the ImageJ + ToolBlock Simulator.
/// </summary>
public enum VisionToolKind : byte
{
    ShapePatternMatch = 0,
    DualRakeCornerAlign = 1,
    RleBlobAnalyzer = 2,
    BinaryPixelCounter = 3,
    SubpixelCaliperMetrology = 4,
    GoldenSurfaceInspector = 5,
    GeometryMathCombiner = 6,
    RelativeOffsetKinematics = 7,
    ToleranceSpecJudge = 8,
    FinalVerdictAggregator = 9
}
