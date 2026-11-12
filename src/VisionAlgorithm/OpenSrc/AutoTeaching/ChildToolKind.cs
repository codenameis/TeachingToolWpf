using System.Numerics;
using VisionAlgorithm.OpenSrc.Common;

namespace VisionAlgorithm.OpenSrc.AutoTeaching;

/// <summary>
/// Inspection tool type attached to a child ROI template relative to a parent part model.
/// </summary>
public enum ChildToolKind : byte
{
    SubpixelCaliperWidth = 0,
    GoldenDifferenceAnomaly = 1,
    RleBlobPinInspection = 2,
    FiducialCrosshairCheck = 3
}
