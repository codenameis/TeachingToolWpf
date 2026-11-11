using System.Collections.Immutable;
using VisionGeometry;

namespace VisionOcr.PpOcr;

/// <summary>
/// Detected text polygon and recognized text for one line crop inside an ROI.
/// </summary>
public sealed record PpOcrBoxResult(
    ImmutableArray<PointD> Polygon,
    string Text,
    float MeanConfidence,
    float MinConfidence,
    float DetScore,
    bool UsedInvertedPolarity);
