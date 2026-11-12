namespace TeachingToolWpf.Imaging.Matching;

/// <summary>
/// 1번 서브픽셀 캘리퍼·RANSAC 직선·Taubin 원 계측 전용 옵션 및 범위 상수.
/// </summary>
public sealed record MetrologyOptions(
    float GaussianSigma = MetrologyOptions.DefaultGaussianSigma,
    float MinimumContrast = MetrologyOptions.DefaultMinimumContrast,
    MetrologyMeasurementMode Mode = MetrologyMeasurementMode.Auto,
    MetrologyPolarityMode PolarityMode = MetrologyPolarityMode.Any,
    int CaliperRakeCount = MetrologyOptions.DefaultCaliperRakeCount,
    float CaliperProjectionWidth = MetrologyOptions.DefaultCaliperProjectionWidth)
{
    public const float DefaultGaussianSigma = 1.2f;
    public const float MinGaussianSigma = 0.5f;
    public const float MaxGaussianSigma = 8.0f;
    public const float UiMinGaussianSigma = 0.4f;
    public const float UiMaxGaussianSigma = 10.0f;

    public const float DefaultMinimumContrast = 18.0f;
    public const float MinMinimumContrast = 2.0f;
    public const float MaxMinimumContrast = 200.0f;
    public const float UiMinMinimumContrast = 1.0f;
    public const float UiMaxMinimumContrast = 250.0f;

    public const int DefaultCaliperRakeCount = 15;
    public const int MinRakeCount = 5;
    public const int MaxRakeCount = 48;
    public const int MinSpokeCount = 8;
    public const int MaxSpokeCount = 48;
    public const int MaxGuideRakeCount = 32;
    public const int MaxGuideSpokeCount = 24;

    public const float DefaultCaliperProjectionWidth = 11.0f;
    public const float MinProjectionWidth = 3.0f;
    public const float MaxProjectionWidth = 45.0f;

    public MetrologyOptions Validate() => new(
        GaussianSigma: Math.Clamp(GaussianSigma, MinGaussianSigma, MaxGaussianSigma),
        MinimumContrast: Math.Clamp(MinimumContrast, MinMinimumContrast, MaxMinimumContrast),
        Mode: Mode,
        PolarityMode: PolarityMode,
        CaliperRakeCount: Math.Clamp(CaliperRakeCount, MinRakeCount, MaxRakeCount),
        CaliperProjectionWidth: Math.Clamp(CaliperProjectionWidth, MinProjectionWidth, MaxProjectionWidth));

    public string ValidateForUi()
    {
        if (!float.IsFinite(GaussianSigma) || GaussianSigma < UiMinGaussianSigma || GaussianSigma > UiMaxGaussianSigma)
        {
            return "가우시안 시그마(σ)는 0.4 ~ 10.0 범위여야 합니다.";
        }

        if (!float.IsFinite(MinimumContrast) || MinimumContrast < UiMinMinimumContrast || MinimumContrast > UiMaxMinimumContrast)
        {
            return "최소 에지 대비(Contrast)는 1.0 ~ 250.0 범위여야 합니다.";
        }

        return string.Empty;
    }
}
