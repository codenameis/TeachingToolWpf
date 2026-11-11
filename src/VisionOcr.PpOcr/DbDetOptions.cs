namespace VisionOcr.PpOcr;

/// <summary>
/// Official DBNet post-processing options for PP-OCRv4 text detection.
/// </summary>
public sealed record DbDetOptions(
    float BinThreshold = DbDetOptions.DefaultBinThreshold,
    float BoxThreshold = DbDetOptions.DefaultBoxThreshold,
    float UnclipRatio = DbDetOptions.DefaultUnclipRatio,
    float MinBoxSidePx = DbDetOptions.DefaultMinBoxSidePx,
    float MinBoxAreaPx = DbDetOptions.DefaultMinBoxAreaPx,
    int MaxCandidates = DbDetOptions.DefaultMaxCandidates,
    int LimitSideLen = DbDetOptions.DefaultLimitSideLen,
    int BorderPadPx = DbDetOptions.DefaultBorderPadPx)
{
    public const float DefaultBinThreshold = 0.30f;
    public const float DefaultBoxThreshold = 0.50f;
    public const float DefaultUnclipRatio = 1.60f;
    public const float DefaultMinBoxSidePx = 3.0f;
    public const float DefaultMinBoxAreaPx = 12.0f;
    public const int DefaultMaxCandidates = 1000;
    public const int DefaultLimitSideLen = 960;
    public const int DefaultBorderPadPx = 12;
    public const int StrideAlignment = 32;

    public static DbDetOptions Default { get; } = new();
}
