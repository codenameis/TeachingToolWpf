// File: src/VisionAlgorithm/OpenSrc/Common/VisionExecutionStatus.cs
namespace VisionAlgorithm.OpenSrc.Common;

/// <summary>
/// Represents the deterministic execution and feature detection status of any vision algorithm step.
/// Separates geometric/feature detection success from tolerance pass/fail judgment (<see cref="VisionVerdict"/>).
/// </summary>
public enum VisionExecutionStatus : byte
{
    Uninitialized = 0,
    Succeeded = 1,
    NotFound = 2,
    DegenerateGeometry = 3,
    InvalidInput = 4
}
