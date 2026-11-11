namespace VisionOcr.PpOcr;

/// <summary>
/// Pipeline stage routing for ROI recognition.
/// </summary>
public enum PpOcrPipelineMode
{
    Auto = 0,
    RecOnly = 1,
    DetAndRec = 2,
}
