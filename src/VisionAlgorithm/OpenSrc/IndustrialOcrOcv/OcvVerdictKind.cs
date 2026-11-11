namespace VisionAlgorithm.OpenSrc.IndustrialOcrOcv;

/// <summary>
/// Tri-state industrial Optical Character Verification (OCV) verdict distinguishing
/// marginal review/warning conditions from hard product defects (ADR-057).
/// </summary>
public enum OcvVerdictKind
{
    Ok = 0,
    Review = 1,
    Ng = 2,
}
