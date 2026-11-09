// File: src/VisionAlgorithm/OpenSrc/PatMax/PatMaxTrainParams.cs
namespace VisionAlgorithm.OpenSrc.PatMax;

/// <summary>
/// Validated training configuration for the multi-scale edge dipole and attraction field engine (US 6,408,109 / US 7,065,262).
/// </summary>
public readonly record struct PatMaxTrainParams
{
    public const int DefaultCoarseGranularity = 4;
    public const int DefaultFineGranularity = 1;
    public const float DefaultEdgeThreshold = 15.0f;
    public const float DefaultElasticityPixels = 2.5f;
    public const int DefaultMinFeatureCount = 8;
    public const float DefaultMaxConditionNumber = 25.0f;
    public const int MaxAllowedGranularity = 16;
    public const float DefaultFilterSigma = 1.0f;

    public int CoarseGranularity { get; }
    public int FineGranularity { get; }
    public float EdgeThreshold { get; }
    public float ElasticityPixels { get; }
    public int MinFeatureCount { get; }
    public float MaxConditionNumber { get; }
    public PatMaxPreprocessingFilterKind PreprocessingFilter { get; }
    public float FilterSigma { get; }
    public bool IsEmpty { get; }

    private PatMaxTrainParams(
        int coarseGranularity,
        int fineGranularity,
        float edgeThreshold,
        float elasticityPixels,
        int minFeatureCount,
        float maxConditionNumber,
        PatMaxPreprocessingFilterKind preprocessingFilter,
        float filterSigma,
        bool isEmpty)
    {
        CoarseGranularity = coarseGranularity;
        FineGranularity = fineGranularity;
        EdgeThreshold = edgeThreshold;
        ElasticityPixels = elasticityPixels;
        MinFeatureCount = minFeatureCount;
        MaxConditionNumber = maxConditionNumber;
        PreprocessingFilter = preprocessingFilter;
        FilterSigma = filterSigma;
        IsEmpty = isEmpty;
    }

    public static PatMaxTrainParams Empty => new(0, 0, 0f, 0f, 0, 0f, PatMaxPreprocessingFilterKind.None, 0f, isEmpty: true);

    public static PatMaxTrainParams Default => Create(
        DefaultCoarseGranularity,
        DefaultFineGranularity,
        DefaultEdgeThreshold,
        DefaultElasticityPixels,
        DefaultMinFeatureCount,
        DefaultMaxConditionNumber);

    public static PatMaxTrainParams Create(
        int coarseGranularity = DefaultCoarseGranularity,
        int fineGranularity = DefaultFineGranularity,
        float edgeThreshold = DefaultEdgeThreshold,
        float elasticityPixels = DefaultElasticityPixels,
        int minFeatureCount = DefaultMinFeatureCount,
        float maxConditionNumber = DefaultMaxConditionNumber,
        PatMaxPreprocessingFilterKind preprocessingFilter = PatMaxPreprocessingFilterKind.None,
        float filterSigma = DefaultFilterSigma)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fineGranularity);
        ArgumentOutOfRangeException.ThrowIfLessThan(coarseGranularity, fineGranularity);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(coarseGranularity, MaxAllowedGranularity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(edgeThreshold);
        ArgumentOutOfRangeException.ThrowIfNegative(elasticityPixels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minFeatureCount);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxConditionNumber, 1f);
        if (!Enum.IsDefined(preprocessingFilter))
        {
            throw new ArgumentOutOfRangeException(nameof(preprocessingFilter));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(filterSigma);

        return new PatMaxTrainParams(
            coarseGranularity,
            fineGranularity,
            edgeThreshold,
            elasticityPixels,
            minFeatureCount,
            maxConditionNumber,
            preprocessingFilter,
            filterSigma,
            isEmpty: false);
    }
}
