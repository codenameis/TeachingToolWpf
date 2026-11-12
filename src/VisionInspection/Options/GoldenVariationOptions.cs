namespace TeachingToolWpf.Imaging.Matching;

/// <summary>
/// 2번 통계적 골든 마스터 변이 검사(Welford + Sobel) 전용 옵션 및 범위 상수.
/// </summary>
public sealed record GoldenVariationOptions(
    float VariationStdMultiplier = GoldenVariationOptions.DefaultVariationStdMultiplier,
    float VariationEdgeMultiplier = GoldenVariationOptions.DefaultVariationEdgeMultiplier,
    int MinimumDefectAreaPixels = GoldenVariationOptions.DefaultMinimumDefectAreaPixels)
{
    public const float DefaultVariationStdMultiplier = 3.0f;
    public const float MinVariationStdMultiplier = 1.0f;
    public const float MaxVariationStdMultiplier = 10.0f;

    public const float DefaultVariationEdgeMultiplier = 0.5f;
    public const float MinVariationEdgeMultiplier = 0.0f;
    public const float MaxVariationEdgeMultiplier = 3.0f;

    public const int DefaultMinimumDefectAreaPixels = 6;
    public const int MinDefectAreaPixels = 1;
    public const int MaxDefectAreaPixels = 500;

    public GoldenVariationOptions Validate() => new(
        VariationStdMultiplier: Math.Clamp(VariationStdMultiplier, MinVariationStdMultiplier, MaxVariationStdMultiplier),
        VariationEdgeMultiplier: Math.Clamp(VariationEdgeMultiplier, MinVariationEdgeMultiplier, MaxVariationEdgeMultiplier),
        MinimumDefectAreaPixels: Math.Clamp(MinimumDefectAreaPixels, MinDefectAreaPixels, MaxDefectAreaPixels));
}
