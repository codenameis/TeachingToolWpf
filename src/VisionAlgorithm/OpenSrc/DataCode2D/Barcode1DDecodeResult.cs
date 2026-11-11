namespace VisionAlgorithm.OpenSrc.DataCode2D;

/// <summary>
/// Result of multi-scanline 1D barcode (Code 128 / EAN-13) decoding, oriented 2D world pose (center, size, rotation angle),
/// and ISO/IEC 15416 print quality grading.
/// </summary>
public readonly record struct Barcode1DDecodeResult(
    bool IsDecoded,
    string Symbology,
    string Payload,
    int ChecksumValue,
    bool IsChecksumValid,
    int DecodedScanlineCount,
    double SymbolContrast,
    double Modulation,
    double Decodability,
    double OverallGradeScore,
    string IsoGradeLetter,
    double StartX,
    double EndX,
    double ScanlineY,
    double TopY = 0.0,
    double BottomY = 0.0,
    double AngleRad = 0.0,
    double OrientedCenterX = double.NaN,
    double OrientedCenterY = double.NaN,
    double OrientedWidth = double.NaN,
    double OrientedHeight = double.NaN)
{
    private const double RadiansToDegreesFactor = 180.0 / Math.PI;
    private const double DefaultHalfBarSpan = 24.0;
    private const double HalfFactor = 0.5;
    private const double MinimumBarHeight = 1.0;

    public double EffectiveTopY => TopY > 0.0 || BottomY > 0.0 ? TopY : Math.Max(0.0, ScanlineY - DefaultHalfBarSpan);

    public double EffectiveBottomY => BottomY > TopY ? BottomY : ScanlineY + DefaultHalfBarSpan;

    public double CenterX => !double.IsNaN(OrientedCenterX) ? OrientedCenterX : (StartX + EndX) * HalfFactor;

    public double CenterY => !double.IsNaN(OrientedCenterY) ? OrientedCenterY : (EffectiveTopY + EffectiveBottomY) * HalfFactor;

    public double WidthPixels => !double.IsNaN(OrientedWidth) ? Math.Max(0.0, OrientedWidth) : Math.Max(0.0, EndX - StartX);

    public double HeightPixels => !double.IsNaN(OrientedHeight) ? Math.Max(MinimumBarHeight, OrientedHeight) : Math.Max(MinimumBarHeight, EffectiveBottomY - EffectiveTopY);

    public double AngleDeg => AngleRad * RadiansToDegreesFactor;
}
