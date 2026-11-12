using System.Collections.Immutable;
using VisionGeometry;

namespace VisionOcr.PpOcr;

/// <summary>
/// Polygon and recognized text for one text crop inside an ROI.
/// When <see cref="IsDetectedQuad"/> is <c>false</c> (<c>RecOnly</c> mode), <see cref="DetScore"/> is <c>0f</c>.
/// </summary>
public sealed record PpOcrBoxResult(
    ImmutableArray<PointD> Polygon,
    string Text,
    float MeanConfidence,
    float MinConfidence,
    float DetScore,
    bool UsedInvertedPolarity,
    bool IsDetectedQuad = true);
