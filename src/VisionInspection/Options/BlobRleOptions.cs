namespace TeachingToolWpf.Imaging.Matching;

/// <summary>
/// 3번 RLE 스캔라인 블롭 해석 및 구간 모폴로지 전용 옵션 및 범위 상수.
/// </summary>
public sealed record BlobRleOptions(
    int ThresholdIntensity = BlobRleOptions.DefaultThresholdIntensity,
    int MorphologyRadius = BlobRleOptions.DefaultMorphologyRadius,
    int MinimumAreaPixels = BlobRleOptions.DefaultMinimumAreaPixels)
{
    public const int DefaultThresholdIntensity = 128;
    public const int MinThresholdIntensity = 1;
    public const int MaxThresholdIntensity = 255;

    public const int DefaultMorphologyRadius = 1;
    public const int MinMorphologyRadius = 0;
    public const int MaxMorphologyRadius = 12;
    public const int UiMaxMorphologyRadius = 16;

    public const int DefaultMinimumAreaPixels = 8;
    public const int MinAreaPixels = 1;
    public const int MaxAreaPixels = 5000;

    public BlobRleOptions Validate() => new(
        ThresholdIntensity: Math.Clamp(ThresholdIntensity, MinThresholdIntensity, MaxThresholdIntensity),
        MorphologyRadius: Math.Clamp(MorphologyRadius, MinMorphologyRadius, MaxMorphologyRadius),
        MinimumAreaPixels: Math.Clamp(MinimumAreaPixels, MinAreaPixels, MaxAreaPixels));

    public string ValidateForUi()
    {
        if (ThresholdIntensity < MinThresholdIntensity || ThresholdIntensity > MaxThresholdIntensity)
        {
            return "RLE 이진화 임계값은 1 ~ 255 범위여야 합니다.";
        }

        if (MorphologyRadius < MinMorphologyRadius || MorphologyRadius > UiMaxMorphologyRadius)
        {
            return "모폴로지 반경은 0 ~ 16 범위여야 합니다.";
        }

        return string.Empty;
    }
}
