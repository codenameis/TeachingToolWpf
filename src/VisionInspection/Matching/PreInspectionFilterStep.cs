namespace TeachingToolWpf.Imaging.Matching;

/// <summary>
/// Immutable parameter descriptor for a single step in the ordered pre-inspection image filter pipeline.
/// </summary>
public readonly record struct PreInspectionFilterStep(
    PreInspectionFilterKind Kind,
    bool IsEnabled,
    double Threshold,
    double EdgeLow,
    double EdgeHigh,
    int Radius,
    double Sigma);
