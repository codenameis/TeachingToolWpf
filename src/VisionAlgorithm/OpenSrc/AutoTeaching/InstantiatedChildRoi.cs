using System.Numerics;
using VisionAlgorithm.OpenSrc.Common;

namespace VisionAlgorithm.OpenSrc.AutoTeaching;

/// <summary>
/// Instantiated world-space child ROI generated automatically when its parent part is recognized on a new board.
/// </summary>
public readonly record struct InstantiatedChildRoi(
    string ChildName,
    ChildToolKind ToolKind,
    FixturePose2D WorldPose,
    Vector2 WorldCenter,
    float Width,
    float Height);
