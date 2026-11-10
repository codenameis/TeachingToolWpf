// File: src/VisionAlgorithm/OpenSrc/Common/VisionVerdict.cs
namespace VisionAlgorithm.OpenSrc.Common;

/// <summary>
/// Unified pass/fail specification verdict across all vision inspection algorithms.
/// Normalizes polarity so <see cref="Pass"/> always means a defect-free / within-tolerance product.
/// </summary>
public enum VisionVerdict : byte
{
    NotEvaluated = 0,
    Pass = 1,
    Fail = 2,
    Warning = 3,
    Error = 4
}
