namespace TeachingToolWpf.Imaging.Matching;

/// <summary>
/// 5번 소량 샘플 분류 및 마할라노비스 OOD 이상 탐지(Few-Shot) 전용 옵션 및 범위 상수.
/// </summary>
public sealed record FewShotSuiteOptions(
    float OodSigmaMultiplier = FewShotSuiteOptions.DefaultOodSigmaMultiplier,
    int MaxSubClusters = FewShotSuiteOptions.DefaultMaxSubClusters)
{
    public const float DefaultOodSigmaMultiplier = 3.5f;
    public const float MinOodSigmaMultiplier = 1.0f;
    public const float MaxOodSigmaMultiplier = 10.0f;

    public const int DefaultMaxSubClusters = 2;
    public const int MinSubClusterCount = 1;
    public const int MaxSubClusterCount = 5;

    public FewShotSuiteOptions Validate() => new(
        OodSigmaMultiplier: Math.Clamp(OodSigmaMultiplier, MinOodSigmaMultiplier, MaxOodSigmaMultiplier),
        MaxSubClusters: Math.Clamp(MaxSubClusters, MinSubClusterCount, MaxSubClusterCount));
}
