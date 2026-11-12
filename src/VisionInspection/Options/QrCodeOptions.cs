namespace TeachingToolWpf.Imaging.Matching;

/// <summary>
/// 21번 산업용 2D QR Code (ISO/IEC 18004) 리더 전용 옵션 및 범위 상수.
/// </summary>
public sealed record QrCodeOptions(
    float MinIsoContrast = QrCodeOptions.DefaultMinIsoContrast,
    int MaxEccErrors = QrCodeOptions.DefaultMaxEccErrors)
{
    public const float DefaultMinIsoContrast = 0.20f;
    public const double MinIsoContrastBound = 0.10;
    public const double MaxIsoContrastBound = 0.90;
    public const float UiMinIsoContrastBound = 0.05f;

    public const int DefaultMaxEccErrors = 8;
    public const int MinEccErrorsBound = 0;
    public const int MaxEccErrorsBound = 32;
    public const int UiMaxEccErrorsBound = 32;

    public QrCodeOptions Validate() => new(
        MinIsoContrast: (float)Math.Clamp((double)MinIsoContrast, MinIsoContrastBound, MaxIsoContrastBound),
        MaxEccErrors: Math.Clamp(MaxEccErrors, MinEccErrorsBound, MaxEccErrorsBound));
}
