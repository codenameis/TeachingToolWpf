// File: src/VisionAlgorithm/OpenSrc/ToolBlock/GeometryCombinerOperation.cs
namespace VisionAlgorithm.OpenSrc.ToolBlock;

/// <summary>
/// Specifies the analytical 2D metrology operation evaluated by <see cref="GeometryCombinerNode"/>.
/// </summary>
public enum GeometryCombinerOperation
{
    PointToPointDistanceAndAngle = 0,
    PointToLineOrthogonalDistance = 1,
    LineToLineIntersection = 2,
    MidpointGenerator = 3
}
