namespace VisionAlgorithm.OpenSrc.DataCode2D;

/// <summary>
/// ISO/IEC 18004 QR Code Reed-Solomon error correction capacity levels.
/// </summary>
public enum QrErrorCorrectionLevel
{
    /// <summary>Low (~7% codeword recovery capacity, format bits 01).</summary>
    L = 0,

    /// <summary>Medium (~15% codeword recovery capacity, format bits 00).</summary>
    M = 1,

    /// <summary>Quartile (~25% codeword recovery capacity, format bits 11).</summary>
    Q = 2,

    /// <summary>High (~30% codeword recovery capacity, format bits 10).</summary>
    H = 3
}
