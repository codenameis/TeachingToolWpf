using System.Numerics;
using VisionAlgorithm.OpenSrc.Common;

namespace VisionAlgorithm.OpenSrc.AutoTeaching;

/// <summary>
/// Defines a child inspection ROI in the local coordinate frame of its parent <see cref="PartPoolEntry"/>.
/// </summary>
public readonly record struct ChildRoiTemplateSpec(
    string ChildName,
    ChildToolKind ToolKind,
    FixturePose2D LocalOffsetPose,
    float Width,
    float Height);
