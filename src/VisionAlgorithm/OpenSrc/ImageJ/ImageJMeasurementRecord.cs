// File: src/VisionAlgorithm/OpenSrc/ImageJ/ImageJMeasurementRecord.cs
namespace VisionAlgorithm.OpenSrc.ImageJ;

/// <summary>
/// Immutable row produced by ImageJ "Analyze &gt; Measure" (<c>Ctrl+M</c>) or ROI Manager "Multi-Measure".
/// Contains area, intensity moments, quantiles, integrated density, geometric centroid, and center of mass.
/// </summary>
public sealed record ImageJMeasurementRecord(
    int MeasurementNumber,
    string Label,
    int RoiX,
    int RoiY,
    int RoiWidth,
    int RoiHeight,
    int AreaPixels,
    float MeanGray,
    float StdDevGray,
    byte MinGray,
    byte MaxGray,
    byte ModeGray,
    byte MedianGray,
    double IntegratedDensity,
    float CentroidX,
    float CentroidY,
    float CenterOfMassX,
    float CenterOfMassY,
    float Skewness,
    float Kurtosis,
    float EntropyBits,
    byte OtsuThreshold,
    float ThresholdedAreaPercent)
{
    public string RoiBoundsText => $"[{RoiX}, {RoiY}, {RoiWidth}×{RoiHeight}]";
    public string CentroidText => $"({CentroidX:F1}, {CentroidY:F1})";
    public string CenterOfMassText => $"({CenterOfMassX:F1}, {CenterOfMassY:F1})";
}
