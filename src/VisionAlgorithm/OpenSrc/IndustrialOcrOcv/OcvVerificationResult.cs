using System.Collections.Generic;

namespace VisionAlgorithm.OpenSrc.IndustrialOcrOcv;

/// <summary>
/// Line-level OCV verification report containing per-character quality metrics, tri-state verdict
/// (<see cref="OcvVerdictKind.Ok"/> / <see cref="OcvVerdictKind.Review"/> / <see cref="OcvVerdictKind.Ng"/>),
/// cause summary, and operator remediation guidance.
/// </summary>
public sealed record OcvVerificationResult(
    OcvVerdictKind Verdict,
    string DecodedText,
    string? ExpectedText,
    IReadOnlyList<OcvCharacterVerification> Characters,
    float MeanMatchScore,
    float MinMatchScore,
    float MinContrastGv,
    float MaxMissingStrokeRatio,
    float MaxBleedRatio,
    string CauseSummary,
    string RecommendedAction)
{
    public bool IsOk => Verdict == OcvVerdictKind.Ok;

    public bool NeedsReview => Verdict == OcvVerdictKind.Review;

    public bool IsNg => Verdict == OcvVerdictKind.Ng;
}
