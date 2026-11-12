namespace TeachingToolWpf.Imaging.Matching;

/// <summary>
/// 16번 산업용 2D DataMatrix ECC 200 리더 전용 옵션 및 범위 상수.
/// </summary>
public sealed record DataMatrixOptions(
    float MinIsoContrast = DataMatrixOptions.DefaultMinIsoContrast,
    int MaxEccErrors = DataMatrixOptions.DefaultMaxEccErrors)
{
    public const float DefaultMinIsoContrast = 0.20f;
    public const double MinIsoContrastBound = 0.10;
    public const double MaxIsoContrastBound = 0.90;
    public const float UiMinIsoContrastBound = 0.05f;

    public const int DefaultMaxEccErrors = 8;
    public const int MinEccErrorsBound = 0;
    public const int MaxEccErrorsBound = 16;
    public const int UiMaxEccErrorsBound = 32;

    public DataMatrixOptions Validate() => new(
        MinIsoContrast: (float)Math.Clamp((double)MinIsoContrast, MinIsoContrastBound, MaxIsoContrastBound),
        MaxEccErrors: Math.Clamp(MaxEccErrors, MinEccErrorsBound, MaxEccErrorsBound));
}
