// File: samples/ToolBlock.Simulator/Models/ImageJParticleRowItem.cs
namespace ToolBlock.Simulator.Models;

/// <summary>
/// Represents an individual connected component row in the ImageJ "Analyze Particles &amp; Shape Descriptors" table.
/// </summary>
public sealed record ImageJParticleRowItem(
    int ParticleIndex,
    int AreaPixels,
    string CentroidWorldText,
    string BoundingBoxText,
    string CircularityText,
    string AspectRatioText,
    string EccentricityText,
    string OrientationDegText,
    string FilterPassBadge,
    string BadgeHex);
