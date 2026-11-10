// File: src/VisionAlgorithm/OpenSrc/ImageJ/ImageJParticleRecord.cs
namespace VisionAlgorithm.OpenSrc.ImageJ;

/// <summary>
/// Immutable connected-component particle measurement produced by ImageJ "Analyze &gt; Analyze Particles".
/// </summary>
public sealed record ImageJParticleRecord(
    int ParticleNumber,
    int AreaPixels,
    float MeanGray,
    byte MinGray,
    byte MaxGray,
    float CentroidX,
    float CentroidY,
    int BoundX,
    int BoundY,
    int BoundWidth,
    int BoundHeight,
    float Circularity,
    float AspectRatio,
    float Eccentricity,
    float Solidity,
    float OrientationDeg)
{
    public string CentroidText => $"({CentroidX:F1}, {CentroidY:F1})";
    public string BoundingBoxText => $"[{BoundX}, {BoundY}, {BoundWidth}×{BoundHeight}]";
}
