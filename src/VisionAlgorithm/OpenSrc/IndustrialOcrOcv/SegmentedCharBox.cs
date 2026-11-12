namespace VisionAlgorithm.OpenSrc.IndustrialOcrOcv;

/// <summary>
/// Bounding box of a segmented character inside the rectified text-line strip.
/// </summary>
public readonly record struct SegmentedCharBox(
    int Index,
    int StartCol,
    int EndCol,
    int TopRow,
    int BottomRow,
    bool PrecededBySpace,
    float InterCharBridgeDensity)
{
    public int Width => (EndCol - StartCol) + 1;
    public int Height => (BottomRow - TopRow) + 1;
}
