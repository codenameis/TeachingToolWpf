// File: src/VisionAlgorithm/OpenSrc/PatMax/PatMaxSearchParams.cs
namespace VisionAlgorithm.OpenSrc.PatMax;

/// <summary>
/// Validated runtime search parameters for PatQuick Coarse Search (US 7,016,539) and PatMax Fine Alignment (US 7,065,262).
/// </summary>
public readonly record struct PatMaxSearchParams
{
    public const float DefaultAcceptThreshold = 0.65f;
    public const float DefaultAngleStartRadians = 0.0f;
    public const float DefaultAngleExtentRadians = 0.0f;
    public const float DefaultScaleMin = 1.0f;
    public const float DefaultScaleMax = 1.0f;
    public const float DefaultRuntimeEdgeThreshold = 10.0f;
    public const int DefaultMaxCandidates = 16;
    public const int DefaultMaxResults = 1;
    public const float DefaultMinSeparationPixels = 4.0f;

    public float AcceptThreshold { get; }
    public float AngleStartRadians { get; }
    public float AngleExtentRadians { get; }
    public float ScaleMin { get; }
    public float ScaleMax { get; }
    public float RuntimeEdgeThreshold { get; }
    public bool IgnorePolarity { get; }
    public int MaxCandidates { get; }
    public int MaxResults { get; }
    public float MinSeparationPixels { get; }
    public bool IsEmpty { get; }

    private PatMaxSearchParams(
        float acceptThreshold,
        float angleStartRadians,
        float angleExtentRadians,
        float scaleMin,
        float scaleMax,
        float runtimeEdgeThreshold,
        bool ignorePolarity,
        int maxCandidates,
        int maxResults,
        float minSeparationPixels,
        bool isEmpty)
    {
        AcceptThreshold = acceptThreshold;
        AngleStartRadians = angleStartRadians;
        AngleExtentRadians = angleExtentRadians;
        ScaleMin = scaleMin;
        ScaleMax = scaleMax;
        RuntimeEdgeThreshold = runtimeEdgeThreshold;
        IgnorePolarity = ignorePolarity;
        MaxCandidates = maxCandidates;
        MaxResults = maxResults;
        MinSeparationPixels = minSeparationPixels;
        IsEmpty = isEmpty;
    }

    public static PatMaxSearchParams Empty => new(0f, 0f, 0f, 0f, 0f, 0f, false, 0, 0, 0f, isEmpty: true);

    public static PatMaxSearchParams Default => Create();

    public static PatMaxSearchParams Create(
        float acceptThreshold = DefaultAcceptThreshold,
        float angleStartRadians = DefaultAngleStartRadians,
        float angleExtentRadians = DefaultAngleExtentRadians,
        float scaleMin = DefaultScaleMin,
        float scaleMax = DefaultScaleMax,
        float runtimeEdgeThreshold = DefaultRuntimeEdgeThreshold,
        bool ignorePolarity = false,
        int maxCandidates = DefaultMaxCandidates,
        int maxResults = DefaultMaxResults,
        float minSeparationPixels = DefaultMinSeparationPixels)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(acceptThreshold, 0.0f);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(acceptThreshold, 1.0f);
        ArgumentOutOfRangeException.ThrowIfNegative(angleExtentRadians);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(scaleMin);
        ArgumentOutOfRangeException.ThrowIfLessThan(scaleMax, scaleMin);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(runtimeEdgeThreshold);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCandidates);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxResults);
        ArgumentOutOfRangeException.ThrowIfNegative(minSeparationPixels);

        return new PatMaxSearchParams(
            acceptThreshold,
            angleStartRadians,
            angleExtentRadians,
            scaleMin,
            scaleMax,
            runtimeEdgeThreshold,
            ignorePolarity,
            maxCandidates,
            maxResults,
            minSeparationPixels,
            isEmpty: false);
    }
}
