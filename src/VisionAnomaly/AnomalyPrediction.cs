namespace VisionAnomaly;

public sealed record AnomalyPrediction(float AppearanceRatio, float MotionRatio, bool MotionAvailable,
    IReadOnlyList<AnomalyArea> Areas, double ProcessingMilliseconds)
{
    public IReadOnlyList<AnomalyArea> MotionAreas { get; init; } = [];
}
