// File: src/VisionAlgorithm/OpenSrc/Common/VisionToolCategory.cs
namespace VisionAlgorithm.OpenSrc.Common;

/// <summary>
/// Standardized taxonomy of industrial vision inspection tool families for unified SPC logging and <c>VisionCanvas</c> replay.
/// </summary>
public enum VisionToolCategory : byte
{
    Unknown = 0,
    PatternMatching = 1,
    BlobAnalysis = 2,
    BinaryPixelCount = 3,
    OcrVerification = 4,
    SubpixelMetrology = 5,
    CornerAlignment = 6,
    AnomalyVariation = 7,
    CurvilinearBead = 8,
    GeometryCombiner = 9,
    ToleranceJudge = 10
}
