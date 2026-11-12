namespace VisionAlgorithm.OpenSrc.IndustrialOcrOcv;

/// <summary>
/// Bitmask flags describing pharmaceutical/industrial OCR &amp; OCV verification failure modes.
/// </summary>
[Flags]
public enum OcvDefectFlags : uint
{
    None = 0,
    LowAcceptanceScore = 1u << 0,
    ConfusableAmbiguity = 1u << 1,
    MissingStrokeOrDot = 1u << 2,
    InkSmear = 1u << 3,
    LowContrast = 1u << 4,
    InvalidCalendarDate = 1u << 5,
    FieldingSyntaxMismatch = 1u << 6,
    SegmentationCountMismatch = 1u << 7
}
