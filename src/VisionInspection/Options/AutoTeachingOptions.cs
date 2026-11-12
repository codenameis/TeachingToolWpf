namespace TeachingToolWpf.Imaging.Matching;

/// <summary>
/// 11번 멀티파츠 풀 자동 인식 및 하위 ROI 자동 티칭 전용 옵션 및 범위 상수.
/// </summary>
public sealed record AutoTeachingOptions(
    float MinGeometricScore = AutoTeachingOptions.DefaultMinGeometricScore,
    float MinAppearanceCosine = AutoTeachingOptions.DefaultMinAppearanceCosine,
    float MaxMahalanobis = AutoTeachingOptions.DefaultMaxMahalanobis)
{
    public const float DefaultMinGeometricScore = 0.45f;
    public const float MinGeometricScoreBound = 0.10f;
    public const float MaxGeometricScoreBound = 0.98f;

    public const float DefaultMinAppearanceCosine = 0.35f;
    public const float MinAppearanceCosineBound = 0.10f;
    public const float MaxAppearanceCosineBound = 0.98f;

    public const float DefaultMaxMahalanobis = 35.0f;
    public const float MinMahalanobisBound = 3.0f;
    public const float MaxMahalanobisBound = 100.0f;

    public AutoTeachingOptions Validate() => new(
        MinGeometricScore: Math.Clamp(MinGeometricScore, MinGeometricScoreBound, MaxGeometricScoreBound),
        MinAppearanceCosine: Math.Clamp(MinAppearanceCosine, MinAppearanceCosineBound, MaxAppearanceCosineBound),
        MaxMahalanobis: Math.Clamp(MaxMahalanobis, MinMahalanobisBound, MaxMahalanobisBound));
}
