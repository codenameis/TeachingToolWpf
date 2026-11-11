namespace VisionAlgorithm.OpenSrc.IndustrialOcrOcv;

/// <summary>
/// Evaluates whether a candidate prototype or vocabulary token belongs to a user-selected <see cref="OcrAllowedCharacterSet"/>.
/// </summary>
public static class OcrCharacterSetFilter
{
    public static bool IncludesLowercase(OcrAllowedCharacterSet characterSet)
        => characterSet == OcrAllowedCharacterSet.All;

    public static string GetDisplayName(OcrAllowedCharacterSet characterSet) => characterSet switch
    {
        OcrAllowedCharacterSet.All => "전체 (대·소문자+숫자+기호)",
        OcrAllowedCharacterSet.UpperAlphaNumeric => "대문자 + 숫자 (A-Z, 0-9)",
        OcrAllowedCharacterSet.UpperAlphaNumericAndSymbols => "대문자 + 숫자 + 기호 (A-Z, 0-9, .-/:)",
        OcrAllowedCharacterSet.DigitsOnly => "숫자 전용 (0-9)",
        OcrAllowedCharacterSet.DigitsAndSeparators => "숫자 + 구분 기호 (0-9, .-/:)",
        OcrAllowedCharacterSet.UpperAlphaOnly => "영문 대문자 전용 (A-Z)",
        _ => "전체"
    };

    public static bool Allows(OcrAllowedCharacterSet characterSet, char candidate) => characterSet switch
    {
        OcrAllowedCharacterSet.All => true,
        OcrAllowedCharacterSet.UpperAlphaNumeric =>
            candidate is (>= 'A' and <= 'Z') or (>= '0' and <= '9') or ' ',
        OcrAllowedCharacterSet.UpperAlphaNumericAndSymbols =>
            candidate is (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '.' or '-' or '/' or ':' or '#' or '$' or '+' or '_' or ' ',
        OcrAllowedCharacterSet.DigitsOnly =>
            candidate is (>= '0' and <= '9') or ' ',
        OcrAllowedCharacterSet.DigitsAndSeparators =>
            candidate is (>= '0' and <= '9') or '.' or '-' or '/' or ':' or ' ',
        OcrAllowedCharacterSet.UpperAlphaOnly =>
            candidate is (>= 'A' and <= 'Z') or ' ',
        _ => true
    };

    public static bool AllowsToken(OcrAllowedCharacterSet characterSet, string token)
    {
        if (characterSet == OcrAllowedCharacterSet.All)
        {
            return true;
        }

        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        for (int i = 0; i < token.Length; i++)
        {
            if (!Allows(characterSet, token[i]))
            {
                return false;
            }
        }

        return true;
    }
}
