namespace TeachingToolWpf.Imaging.Matching;

/// <summary>
/// 15번 RLE 고급 형상 및 EDT 워터셰드 접촉 블롭 분리 전용 옵션 및 범위 상수.
/// </summary>
public sealed record AdvancedBlobOptions(
    int ThresholdIntensity = AdvancedBlobOptions.DefaultThresholdIntensity,
    float MinPeakDistancePx = AdvancedBlobOptions.DefaultMinPeakDistancePx,
    int MinimumAreaPixels = AdvancedBlobOptions.DefaultMinimumAreaPixels)
{
    public const int DefaultThresholdIntensity = 128;
    public const int MinThresholdIntensity = 1;
    public const int MaxThresholdIntensity = 255;
    public const int UiMaxThresholdIntensity = 254;

    public const float DefaultMinPeakDistancePx = 14.0f;
    public const float MinPeakDistanceBoundPx = 4.0f;
    public const float MaxPeakDistanceBoundPx = 48.0f;
    public const float UiMinPeakDistanceBoundPx = 3.0f;
    public const float UiMaxPeakDistanceBoundPx = 80.0f;

    public const int DefaultMinimumAreaPixels = 12;
    public const int MinAreaBoundPixels = 4;
    public const int MaxAreaBoundPixels = 10000;
    public const int UiMinAreaBoundPixels = 1;
    public const int UiMaxAreaBoundPixels = 5000;

    public AdvancedBlobOptions Validate() => new(
        ThresholdIntensity: Math.Clamp(ThresholdIntensity, MinThresholdIntensity, MaxThresholdIntensity),
        MinPeakDistancePx: Math.Clamp(MinPeakDistancePx, MinPeakDistanceBoundPx, MaxPeakDistanceBoundPx),
        MinimumAreaPixels: Math.Clamp(MinimumAreaPixels, MinAreaBoundPixels, MaxAreaBoundPixels));
}
