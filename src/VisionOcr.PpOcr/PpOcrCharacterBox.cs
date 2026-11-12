namespace VisionOcr.PpOcr;

/// <summary>
/// Bounding box and recognition confidence for a single decoded character inside an ROI crop.
/// </summary>
public readonly record struct PpOcrCharacterBox(
    char Character,
    float Confidence,
    int LineIndex,
    int StartCol,
    int EndCol,
    int TopRow,
    int BottomRow,
    bool PrecededBySpace);
