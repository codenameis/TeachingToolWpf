namespace TeachingToolWpf.Imaging.Matching;

/// <summary>
/// 20번 다중 형상 기하공차(GD&amp;T) 직교도·동심도·데이텀 수직거리 계측 전용 옵션 및 범위 상수.
/// </summary>
public sealed record GeometricTolerancingOptions(
    float AngleToleranceDeg = GeometricTolerancingOptions.DefaultAngleToleranceDeg,
    float ConcentricityLimitPx = GeometricTolerancingOptions.DefaultConcentricityLimitPx,
    float DistanceTolerancePx = GeometricTolerancingOptions.DefaultDistanceTolerancePx,
    float GradientThreshold = GeometricTolerancingOptions.DefaultGradientThreshold)
{
    public const float DefaultAngleToleranceDeg = 3.0f;
    public const double MinAngleToleranceDeg = 0.2;
    public const double MaxAngleToleranceDeg = 15.0;

    public const float DefaultConcentricityLimitPx = 2.5f;
    public const double MinConcentricityLimitPx = 0.2;
    public const double MaxConcentricityLimitPx = 10.0;
    public const float UiMaxConcentricityLimitPx = 20.0f;

    public const float DefaultDistanceTolerancePx = 4.0f;
    public const double MinDistanceTolerancePx = 0.5;
    public const double MaxDistanceTolerancePx = 20.0;
    public const float UiMinDistanceTolerancePx = 0.2f;
    public const float UiMaxDistanceTolerancePx = 30.0f;

    public const float DefaultGradientThreshold = 18.0f;
    public const double MinGradientThreshold = 6.0;
    public const double MaxGradientThreshold = 120.0;
    public const float UiMinGradientThreshold = 2.0f;

    public GeometricTolerancingOptions Validate() => new(
        AngleToleranceDeg: (float)Math.Clamp((double)AngleToleranceDeg, MinAngleToleranceDeg, MaxAngleToleranceDeg),
        ConcentricityLimitPx: (float)Math.Clamp((double)ConcentricityLimitPx, MinConcentricityLimitPx, MaxConcentricityLimitPx),
        DistanceTolerancePx: (float)Math.Clamp((double)DistanceTolerancePx, MinDistanceTolerancePx, MaxDistanceTolerancePx),
        GradientThreshold: (float)Math.Clamp((double)GradientThreshold, MinGradientThreshold, MaxGradientThreshold));
}
