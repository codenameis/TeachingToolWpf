namespace VisionAlgorithm.OpenSrc.IndustrialOcrOcv;

/// <summary>
/// User-injected candidate character domain restriction for general OCR classification and CTC decoding.
/// Applied strictly during candidate prototype / vocabulary scoring without post-hoc string replacement.
/// </summary>
public enum OcrAllowedCharacterSet
{
    All = 0,
    UpperAlphaNumeric = 1,
    UpperAlphaNumericAndSymbols = 2,
    DigitsOnly = 3,
    DigitsAndSeparators = 4,
    UpperAlphaOnly = 5
}
