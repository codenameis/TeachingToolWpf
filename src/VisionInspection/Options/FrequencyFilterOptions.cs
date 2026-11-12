namespace TeachingToolWpf.Imaging.Matching;

/// <summary>
/// 17번 2D FFT 모아레 노치·호모모픽 조명 분리·Gabor 텍스처 전용 옵션 및 범위 상수.
/// </summary>
public sealed record FrequencyFilterOptions(
    float NotchOrGaborSigma = FrequencyFilterOptions.DefaultNotchOrGaborSigma,
    float PeakThresholdRatio = FrequencyFilterOptions.DefaultPeakThresholdRatio,
    float HomomorphicGammaHigh = FrequencyFilterOptions.DefaultHomomorphicGammaHigh,
    int MinimumBlobAreaPixels = FrequencyFilterOptions.DefaultMinimumBlobAreaPixels)
{
    public const float DefaultNotchOrGaborSigma = 2.5f;
    public const float MinSigmaBound = 1.0f;
    public const float MaxSigmaBound = 24.0f;
    public const float UiMinSigmaBound = 0.6f;
    public const float UiMaxSigmaBound = 10.0f;

    public const float DefaultPeakThresholdRatio = 4.2f;
    public const double MinPeakRatioBound = 2.0;
    public const double MaxPeakRatioBound = 10.0;
    public const float UiMinPeakRatioBound = 1.5f;
    public const float UiMaxPeakRatioBound = 12.0f;

    public const float DefaultHomomorphicGammaHigh = 1.85f;
    public const double MinGammaHighBound = 1.10;
    public const double MaxGammaHighBound = 3.00;
    public const float UiMinGammaHighBound = 1.0f;
    public const float UiMaxGammaHighBound = 3.5f;

    public const int DefaultMinimumBlobAreaPixels = 8;
    public const int MinPostBlobAreaPixels = 4;

    public FrequencyFilterOptions Validate() => new(
        NotchOrGaborSigma: Math.Clamp(NotchOrGaborSigma, MinSigmaBound, MaxSigmaBound),
        PeakThresholdRatio: (float)Math.Clamp((double)PeakThresholdRatio, MinPeakRatioBound, MaxPeakRatioBound),
        HomomorphicGammaHigh: (float)Math.Clamp((double)HomomorphicGammaHigh, MinGammaHighBound, MaxGammaHighBound),
        MinimumBlobAreaPixels: Math.Max(MinPostBlobAreaPixels, MinimumBlobAreaPixels));
}
