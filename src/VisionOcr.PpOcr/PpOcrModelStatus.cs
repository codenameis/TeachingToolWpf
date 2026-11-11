namespace VisionOcr.PpOcr;

/// <summary>
/// Explicit installation and session readiness state of PP-OCR ONNX models.
/// </summary>
public enum PpOcrModelStatus
{
    Ready = 0,
    MissingModel = 1,
    LoadFailed = 2,
}
