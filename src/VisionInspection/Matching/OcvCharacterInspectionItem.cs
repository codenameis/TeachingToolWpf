using VisionAlgorithm.OpenSrc.IndustrialOcrOcv;

namespace TeachingToolWpf.Imaging.Matching;

/// <summary>
/// Per-character OCV verification breakdown with score, cause, and distinct OK / Review / NG badge.
/// </summary>
public sealed record OcvCharacterInspectionItem(
    int Index,
    char ExpectedChar,
    char ObservedChar,
    OcvVerdictKind Verdict,
    string VerdictBadge,
    string VerdictAccent,
    float MatchScore,
    float ContrastGv,
    float MissingStrokeRatio,
    float BleedRatio,
    string Cause,
    string SummaryLine);
