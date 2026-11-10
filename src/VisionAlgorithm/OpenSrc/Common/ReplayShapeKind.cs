// File: src/VisionAlgorithm/OpenSrc/Common/ReplayShapeKind.cs
namespace VisionAlgorithm.OpenSrc.Common;

/// <summary>
/// Identifies the primary parametric geometry primitive stored in <see cref="VisionReplayPayload"/>
/// for 100% deterministic graphical reconstruction on <c>VisionCanvas</c>.
/// </summary>
public enum ReplayShapeKind : byte
{
    None = 0,
    OrientedBox = 1,
    Circle = 2,
    LineSegment = 3,
    DualLineCorner = 4,
    EdgePair = 5,
    PointTarget = 6,
    Polyline = 7
}
