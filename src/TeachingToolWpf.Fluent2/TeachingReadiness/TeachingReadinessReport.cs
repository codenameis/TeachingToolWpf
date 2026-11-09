// File: TeachingReadiness/TeachingReadinessReport.cs
namespace TeachingToolWpf.Fluent2.TeachingReadiness;

public sealed record TeachingReadinessReport(
    TeachingReadinessGrade Grade,
    float CompositeScore,
    float RmsGradientEnergy,
    float ContrastSpan,
    float StructureIsotropy,
    float DominantOrientationDegrees,
    int EvaluatedPixels,
    string GuidanceSummary);
