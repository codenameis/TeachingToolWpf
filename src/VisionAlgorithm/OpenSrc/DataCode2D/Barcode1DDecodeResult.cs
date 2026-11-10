namespace VisionAlgorithm.OpenSrc.DataCode2D;

/// <summary>
/// Result of multi-scanline 1D barcode (Code 128 / Code 39) decoding and ISO/IEC 15416 print quality grading.
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
    double ScanlineY);
