using System.Collections.Generic;

namespace VisionOcr.PpOcr;

/// <summary>
/// Decoded text lines, character boxes, calibrated CTC probabilities, and pass verdict for a recognized ROI.
/// </summary>
public sealed record PpOcrRegionResult(
    string FullText,
    IReadOnlyList<string> Lines,
    IReadOnlyList<PpOcrCharacterBox> Characters,
    float MeanConfidence,
    float MinConfidence,
    bool DarkOnLight,
    bool IsPass)
{
    public static PpOcrRegionResult CreateEmpty(bool darkOnLight = true)
        => new(string.Empty, [], [], 0f, 0f, darkOnLight, IsPass: false);
}
