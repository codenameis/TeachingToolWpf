namespace TeachingToolWpf.Imaging.Matching;

/// <summary>
/// Result of teaching a golden font line from a user-selected ROI.
/// </summary>
public sealed record OcvTeachingOutcome(
    bool IsSuccess,
    int TaughtGlyphCount,
    int UniqueCharacterCount,
    int ExpectedLength,
    int ObservedSegmentCount,
    string PolarityTag,
    string StatusSummary,
    string FailureCause,
    string RecommendedAction);
