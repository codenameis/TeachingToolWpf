namespace VisionAlgorithm.OpenSrc.IndustrialOcrOcv;

/// <summary>
/// Per-character OCR classification and OCV verification result.
/// </summary>
public readonly record struct CharVerifyResult(
    int CharIndex,
    char DecodedChar,
    char RunnerUpChar,
    float PrimaryScore,
    float RunnerUpScore,
    float ConfusionMargin,
    float OcvQualityScore,
    float ContrastRange,
    float WorstQuadrantRetention,
    float SubpixelShiftX,
    float SubpixelShiftY,
    OcvDefectFlags DefectFlags,
    bool IsPass);
