// File: src/VisionAlgorithm/OpenSrc/Common/VisionSpcRecord.cs
namespace VisionAlgorithm.OpenSrc.Common;

/// <summary>
/// Normalized, flat database/analytics record for Statistical Process Control (SPC) across all vision tool families.
/// Maps 1-to-1 to indexed SQL columns so any algorithm (Pattern Matching, Blob, Binary Count, OCR/OCV, Caliper,
/// Corner Alignment, Golden Variation/PaDiM, Bead Continuity) can be queried and trended uniformly.
/// </summary>
public readonly record struct VisionSpcRecord(
    int NodeId,
    Guid RoiId,
    VisionResultHeader Header,
    short TotalModelCount,
    short RunnerUpModelIndex,
    float RunnerUpScore,
    float WorldX,
    float WorldY,
    float WorldAngleDeg,
    float Scale,
    float DeltaX,
    float DeltaY,
    float DeltaAngleDeg,
    double PrimaryMetric,
    double SpecLsl,
    double SpecUsl,
    float SpecMarginRatio,
    float MeanGrayValue,
    float ContrastOrStdDev,
    float RmsFitErrorPixels,
    float AuxMetric1,
    float AuxMetric2,
    float AuxMetric3,
    int HitCount,
    int TotalSampleCount,
    string DecodedText)
{
    private const double MinValidSpecSpan = 1e-9;

    public VisionToolCategory Category => Header.Category;
    public VisionExecutionStatus Status => Header.Status;
    public VisionVerdict Verdict => Header.Verdict;
    public bool IsFound => Header.IsFound;
    public bool IsPass => Header.IsPass;
    public short MatchedModelIndex => Header.MatchedModelIndex;
    public int MatchedModelId => Header.MatchedModelId;
    public float QualityScore => Header.QualityScore;
    public float ModelSeparationMargin => RunnerUpModelIndex >= 0 ? MathF.Max(0.0f, Header.QualityScore - RunnerUpScore) : Header.QualityScore;

    /// <summary>
    /// Computes the normalized specification margin ratio $\frac{\min(x - \text{LSL}, \text{USL} - x)}{(\text{USL} - \text{LSL}) / 2}$.
    /// Values in <c>[0, 1]</c> are inside tolerance (<c>1.0</c> = exact nominal center, <c>&lt; 0.15</c> = marginal pass warning, <c>&lt; 0</c> = out of spec).
    /// </summary>
    public static float ComputeSpecMarginRatio(double value, double lsl, double usl)
    {
        if (!double.IsFinite(value) || !double.IsFinite(lsl) || !double.IsFinite(usl))
        {
            return float.NaN;
        }

        double halfSpan = (usl - lsl) * 0.5;
        if (halfSpan <= MinValidSpecSpan)
        {
            return 0.0f;
        }

        double marginToNearestLimit = Math.Min(value - lsl, usl - value);
        return (float)(marginToNearestLimit / halfSpan);
    }
}
