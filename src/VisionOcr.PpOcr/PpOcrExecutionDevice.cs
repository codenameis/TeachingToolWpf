namespace VisionOcr.PpOcr;

/// <summary>
/// Execution provider target for PP-OCR ONNX Runtime sessions.
/// </summary>
public enum PpOcrExecutionDevice
{
    Cpu = 0,
    Cuda = 1,
}
