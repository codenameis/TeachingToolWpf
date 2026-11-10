// File: src/VisionAlgorithm/OpenSrc/Common/ImageJAutoThresholdMethod.cs
namespace VisionAlgorithm.OpenSrc.Common;

/// <summary>
/// ImageJ-compatible automatic histogram thresholding algorithms selectable per ROI or ToolBlock node.
/// </summary>
public enum ImageJAutoThresholdMethod : byte
{
    Manual = 0,
    Otsu = 1,
    Triangle = 2,
    IsoData = 3,
    MaxEntropy = 4,
    Mean = 5,
    Percentile = 6
}
