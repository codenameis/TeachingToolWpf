namespace VisionAlgorithm.OpenSrc.IndustrialOcrOcv;

/// <summary>
/// Bounding box and recognition result for a single character inside a rectified ROI.
/// </summary>
public readonly record struct RecognizedCharBox(
    char Character,
    float Confidence,
    int LineIndex,
    int StartCol,
    int EndCol,
    int TopRow,
    int BottomRow,
    bool PrecededBySpace);
