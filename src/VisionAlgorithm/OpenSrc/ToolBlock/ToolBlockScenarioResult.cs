// File: src/VisionAlgorithm/OpenSrc/ToolBlock/ToolBlockScenarioResult.cs
using VisionAlgorithm.OpenSrc.Common;

namespace VisionAlgorithm.OpenSrc.ToolBlock;

/// <summary>
/// Rich snapshot of all intermediate terminal values and the final verdict from an execution of <see cref="ToolBlockScenarioPipeline"/>.
/// </summary>
public readonly record struct ToolBlockScenarioResult(
    bool PatMaxFound,
    FixturePose2D PatMaxPose,
    Point2D PatMaxCenter,
    double PatMaxAngleDeg,
    double PatMaxScore,
    bool BlobFound,
    double BlobCount,
    Point2D BlobWorldCenter,
    double BlobAreaPixels,
    double BlobAngleDeg,
    double BlobEccentricity,
    double MeasuredDistanceMm,
    double MeasuredSlopeAngleDeg,
    Point2D MidpointWorld,
    double LocalDeltaXMm,
    double LocalDeltaYMm,
    double RelativeDeltaThetaDeg,
    double RelativeOffsetMagnitudeMm,
    double ActuatorStrokeUMm,
    double ActuatorStrokeVMm,
    double ActuatorStrokeWMm,
    bool IsDistanceSpecOk,
    bool IsBlobAreaSpecOk,
    bool IsOffsetSpecOk,
    ToolBlockInspectionResult FinalVerdict);
