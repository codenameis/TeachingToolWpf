// File: src/VisionOcr.PpOcr/PpOcrSpeedPreset.cs
namespace VisionOcr.PpOcr;

/// <summary>
/// Operator-facing execution preset balancing zero-teaching versatility against inline industrial tact time.
/// </summary>
public enum PpOcrSpeedPreset
{
    /// <summary>
    /// General-purpose mode: automatic single/multi-line routing, auto-polarity, multilingual confidence selection, and 180-degree check.
    /// </summary>
    UniversalAuto = 0,

    /// <summary>
    /// Inline production tact-time mode: direct <c>RecOnly</c> recognition, single polarity pass, single recognizer pass, upright-only, and multi-ROI batching.
    /// </summary>
    HighSpeedProduction = 1
}
