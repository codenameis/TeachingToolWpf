namespace TeachingToolWpf.Imaging.Matching;

/// <summary>
/// 4번 Steger 헤시안 곡선 능선 및 실링 비드 연속성 검사 전용 옵션 및 범위 상수.
/// </summary>
public sealed record CurvilinearOptions(
    float GaussianSigma = CurvilinearOptions.DefaultGaussianSigma,
    float MinimumContrast = CurvilinearOptions.DefaultMinimumContrast,
    float NominalBeadWidth = CurvilinearOptions.DefaultNominalBeadWidth)
{
    public const float DefaultGaussianSigma = 1.2f;
    public const float MinGaussianSigma = 0.7f;
    public const float MaxGaussianSigma = 6.0f;

    public const float DefaultMinimumContrast = 18.0f;
    public const float MinMinimumContrast = 3.0f;
    public const float MaxMinimumContrast = 150.0f;

    public const float DefaultNominalBeadWidth = 14.0f;
    public const float MinNominalBeadWidth = 3.0f;
    public const float MaxNominalBeadWidth = 120.0f;

    public CurvilinearOptions Validate() => new(
        GaussianSigma: Math.Clamp(GaussianSigma, MinGaussianSigma, MaxGaussianSigma),
        MinimumContrast: Math.Clamp(MinimumContrast, MinMinimumContrast, MaxMinimumContrast),
        NominalBeadWidth: Math.Clamp(NominalBeadWidth, MinNominalBeadWidth, MaxNominalBeadWidth));
}
