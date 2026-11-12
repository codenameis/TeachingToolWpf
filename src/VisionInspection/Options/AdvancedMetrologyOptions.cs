namespace TeachingToolWpf.Imaging.Matching;

/// <summary>
/// 14번 복합 스코어링 캘리퍼 및 Fitzgibbon 타원 피팅 전용 옵션 및 범위 상수.
/// </summary>
public sealed record AdvancedMetrologyOptions(
    float GaussianSigma = AdvancedMetrologyOptions.DefaultGaussianSigma,
    float MinimumContrast = AdvancedMetrologyOptions.DefaultMinimumContrast,
    float CaliperProjectionWidth = AdvancedMetrologyOptions.DefaultCaliperProjectionWidth,
    int CaliperRakeCount = AdvancedMetrologyOptions.DefaultCaliperRakeCount,
    float CompositeExpectedSpanPx = AdvancedMetrologyOptions.DefaultCompositeExpectedSpanPx,
    int EllipseSpokeCount = AdvancedMetrologyOptions.DefaultEllipseSpokeCount,
    float EllipseMaxRmsErrorPx = AdvancedMetrologyOptions.DefaultEllipseMaxRmsErrorPx)
{
    public const float DefaultGaussianSigma = 1.2f;
    public const float MinGaussianSigma = 0.5f;
    public const float MaxGaussianSigma = 8.0f;

    public const float DefaultMinimumContrast = 18.0f;
    public const float MinMinimumContrast = 4.0f;
    public const float MaxMinimumContrast = 180.0f;

    public const float DefaultCaliperProjectionWidth = 11.0f;
    public const int MinProjectionStrips = 3;
    public const int MaxProjectionStrips = 45;

    public const int DefaultCaliperRakeCount = 15;
    public const int MinGuideSpokes = 12;
    public const int MaxGuideSpokes = 32;

    public const float DefaultCompositeExpectedSpanPx = 28.0f;
    public const float MinCompositeExpectedSpanPx = 6.0f;
    public const float MaxCompositeExpectedSpanPx = 160.0f;
    public const float UiMinCompositeExpectedSpanPx = 4.0f;

    public const int DefaultEllipseSpokeCount = 24;
    public const int MinEllipseSpokeCount = 12;
    public const int MaxEllipseSpokeCount = 48;
    public const int UiMinEllipseSpokeCount = 8;
    public const int UiMaxEllipseSpokeCount = 64;

    public const float DefaultEllipseMaxRmsErrorPx = 1.8f;
    public const float MinEllipseMaxRmsErrorPx = 0.2f;
    public const float MaxEllipseMaxRmsErrorPx = 5.0f;
    public const float UiMaxEllipseMaxRmsErrorPx = 8.0f;

    public AdvancedMetrologyOptions Validate() => new(
        GaussianSigma: Math.Clamp(GaussianSigma, MinGaussianSigma, MaxGaussianSigma),
        MinimumContrast: Math.Clamp(MinimumContrast, MinMinimumContrast, MaxMinimumContrast),
        CaliperProjectionWidth: Math.Clamp(CaliperProjectionWidth, MinProjectionStrips, MaxProjectionStrips),
        CaliperRakeCount: Math.Clamp(CaliperRakeCount, MinGuideSpokes, MaxGuideSpokes),
        CompositeExpectedSpanPx: Math.Clamp(CompositeExpectedSpanPx, MinCompositeExpectedSpanPx, MaxCompositeExpectedSpanPx),
        EllipseSpokeCount: Math.Clamp(EllipseSpokeCount, MinEllipseSpokeCount, MaxEllipseSpokeCount),
        EllipseMaxRmsErrorPx: Math.Clamp(EllipseMaxRmsErrorPx, MinEllipseMaxRmsErrorPx, MaxEllipseMaxRmsErrorPx));
}
