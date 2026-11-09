// File: src/VisionAlgorithm/OpenSrc/CornerAlignment/Domain/CornerDetectionOptions.cs
namespace VisionAlgorithm.OpenSrc.CornerAlignment.Domain;

/// <summary>
/// 2-Rake 서브픽셀 가상 코너 검출 및 강건 직선 피팅 하이퍼파라미터 설정.
/// </summary>
public readonly record struct CornerDetectionOptions(
    float GaussianSigma,
    float MinimumContrast,
    float RansacInlierThresholdPixels,
    int TukeyIterations,
    float OrthogonalityToleranceDeg,
    float MinimumInlierRatio = CornerDetectionOptions.DefaultMinimumInlierRatio)
{
    public const float DefaultGaussianSigma = 1.2f;
    public const float DefaultMinimumContrast = 15.0f;
    public const float DefaultRansacInlierThresholdPixels = 0.65f;
    public const int DefaultTukeyIterations = 5;
    public const float DefaultOrthogonalityToleranceDeg = 3.5f;
    public const float DefaultMinimumInlierRatio = 0.50f;

    /// <summary>
    /// 표준 산업용 서브픽셀 가상 코너 검출 기본 옵션을 반환한다.
    /// </summary>
    public static CornerDetectionOptions Default => new(
        GaussianSigma: DefaultGaussianSigma,
        MinimumContrast: DefaultMinimumContrast,
        RansacInlierThresholdPixels: DefaultRansacInlierThresholdPixels,
        TukeyIterations: DefaultTukeyIterations,
        OrthogonalityToleranceDeg: DefaultOrthogonalityToleranceDeg,
        MinimumInlierRatio: DefaultMinimumInlierRatio);
}
