// File: src/VisionAlgorithm/OpenSrc/Common/RoiPreFilterKind.cs
namespace VisionAlgorithm.OpenSrc.Common;

/// <summary>
/// ImageJ-compatible spatial preprocessing filters that can be applied to an ROI prior to histogram analysis or ToolBlock execution.
/// </summary>
public enum RoiPreFilterKind : byte
{
    None = 0,
    Gaussian3x3 = 1,
    Median3x3 = 2,
    SobelGradient = 3,
    UnsharpSharpen3x3 = 4,
    MorphErode3x3 = 5,
    MorphDilate3x3 = 6,
    MorphOpen3x3 = 7,
    MorphClose3x3 = 8,
    RollingBallTopHat = 9,
    Invert = 10,
    EnhanceContrastStretch = 11
}
