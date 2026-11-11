namespace VisionAlgorithm.OpenSrc.IndustrialOcrOcv;

/// <summary>
/// Per-character OCV measurement and verdict against a taught <see cref="IndustrialOcrFontModel"/>.
/// </summary>
public readonly record struct OcvCharacterVerification(
    int CharIndex,
    char? ExpectedChar,
    char ObservedChar,
    float MatchScore,
    float ConfusionMargin,
    float MissingStrokeRatio,
    float BleedRatio,
    float LocalContrastGv,
    int StartCol,
    int EndCol,
    int TopRow,
    int BottomRow,
    bool PrecededBySpace,
    OcvVerdictKind Verdict,
    string Cause);
