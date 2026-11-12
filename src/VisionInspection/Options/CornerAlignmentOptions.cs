namespace TeachingToolWpf.Imaging.Matching;

/// <summary>
/// 8번 2-Rake 서브픽셀 가상 코너 및 대각 정합 전용 옵션 및 범위 상수.
/// </summary>
public sealed record CornerAlignmentOptions(
    float GaussianSigma = CornerAlignmentOptions.DefaultGaussianSigma,
    float MinimumContrast = CornerAlignmentOptions.DefaultMinimumContrast,
    int CaliperRakeCount = CornerAlignmentOptions.DefaultCaliperRakeCount,
    float CaliperProjectionWidth = CornerAlignmentOptions.DefaultCaliperProjectionWidth,
    float ApexIgnoreMargin = CornerAlignmentOptions.DefaultApexIgnoreMargin,
    float RansacThresholdPixels = CornerAlignmentOptions.DefaultRansacThresholdPixels,
    float OrthogonalityToleranceDeg = CornerAlignmentOptions.DefaultOrthogonalityToleranceDeg)
{
    public const float DefaultGaussianSigma = 1.2f;
    public const float MinGaussianSigma = 0.5f;
    public const float MaxGaussianSigma = 8.0f;

    public const float DefaultMinimumContrast = 18.0f;
    public const float MinMinimumContrast = 2.0f;
    public const float MaxMinimumContrast = 200.0f;

    public const int DefaultCaliperRakeCount = 15;
    public const int MinActiveCaliperCount = 10;
    public const int MinGuideTeethCount = 5;
    public const int MaxGuideTeethCount = 16;

    public const float DefaultCaliperProjectionWidth = 11.0f;
    public const float MinProjectionWidth = 3.0f;
    public const float MaxProjectionWidth = 25.0f;

    public const float DefaultApexIgnoreMargin = 36.0f;
    public const float MinApexIgnoreMargin = 16.0f;
    public const float MaxApexIgnoreMargin = 80.0f;

    public const float DefaultRansacThresholdPixels = 0.55f;
    public const float MinRansacThresholdPixels = 0.1f;
    public const float MaxRansacThresholdPixels = 5.0f;

    public const float DefaultOrthogonalityToleranceDeg = 5.0f;
    public const float MinOrthogonalityToleranceDeg = 0.5f;
    public const float MaxOrthogonalityToleranceDeg = 25.0f;

    public CornerAlignmentOptions Validate() => new(
        GaussianSigma: Math.Clamp(GaussianSigma, MinGaussianSigma, MaxGaussianSigma),
        MinimumContrast: Math.Clamp(MinimumContrast, MinMinimumContrast, MaxMinimumContrast),
        CaliperRakeCount: Math.Max(MinActiveCaliperCount, CaliperRakeCount),
        CaliperProjectionWidth: Math.Clamp(CaliperProjectionWidth, MinProjectionWidth, MaxProjectionWidth),
        ApexIgnoreMargin: Math.Clamp(ApexIgnoreMargin, MinApexIgnoreMargin, MaxApexIgnoreMargin),
        RansacThresholdPixels: Math.Clamp(RansacThresholdPixels, MinRansacThresholdPixels, MaxRansacThresholdPixels),
        OrthogonalityToleranceDeg: Math.Clamp(OrthogonalityToleranceDeg, MinOrthogonalityToleranceDeg, MaxOrthogonalityToleranceDeg));
}
