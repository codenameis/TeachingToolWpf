namespace TeachingToolWpf.Imaging.Matching;

/// <summary>
/// 18번 산업용 1D 바코드(Code 128) 멀티스캔 디코더 전용 옵션 및 범위 상수.
/// </summary>
public sealed record Barcode1DOptions(
    int ScanlineCount = Barcode1DOptions.DefaultScanlineCount,
    float MinSymbolContrast = Barcode1DOptions.DefaultMinSymbolContrast)
{
    public const int DefaultScanlineCount = 9;
    public const int MinScanlineCount = 3;
    public const int MaxScanlineCount = 25;
    public const int UiMinScanlineCount = 1;

    public const float DefaultMinSymbolContrast = 0.20f;
    public const double MinSymbolContrastBound = 0.10;
    public const double MaxSymbolContrastBound = 0.85;
    public const float UiMinSymbolContrastBound = 0.05f;
    public const float UiMaxSymbolContrastBound = 0.90f;

    public Barcode1DOptions Validate() => new(
        ScanlineCount: Math.Clamp(ScanlineCount, MinScanlineCount, MaxScanlineCount),
        MinSymbolContrast: (float)Math.Clamp((double)MinSymbolContrast, MinSymbolContrastBound, MaxSymbolContrastBound));
}
