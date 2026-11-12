namespace TeachingToolWpf.Imaging.Matching;

/// <summary>
/// 6번 PaDiM 국소 패치 이상치 히트맵 탐지 전용 옵션 및 범위 상수.
/// </summary>
public sealed record PatchAnomalyOptions(
    int PatchSize = PatchAnomalyOptions.DefaultPatchSize,
    float RegularizationLambda = PatchAnomalyOptions.DefaultRegularizationLambda)
{
    public const int DefaultPatchSize = 8;
    public const int MinPatchSize = 4;
    public const int MaxPatchSize = 32;

    public const float DefaultRegularizationLambda = 0.01f;
    public const float MinRegularizationLambda = 0.001f;
    public const float MaxRegularizationLambda = 0.50f;

    public PatchAnomalyOptions Validate() => new(
        PatchSize: Math.Clamp(PatchSize, MinPatchSize, MaxPatchSize),
        RegularizationLambda: Math.Clamp(RegularizationLambda, MinRegularizationLambda, MaxRegularizationLambda));
}
