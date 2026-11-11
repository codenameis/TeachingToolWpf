namespace VisionAlgorithm.OpenSrc.IndustrialOcrOcv;

/// <summary>
/// Multi-line, multi-font, auto-polarity OCR recognition result for a rectified ROI.
/// </summary>
public sealed record UniversalOcrRegionResult(
    string FullText,
    IReadOnlyList<string> Lines,
    IReadOnlyList<RecognizedCharBox> Characters,
    float MeanConfidence,
    float MinConfidence,
    bool DarkOnLight,
    bool IsPass);
