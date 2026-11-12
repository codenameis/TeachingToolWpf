using VisionAlgorithm.OpenSrc.IndustrialOcrOcv;

namespace VisionOcr.PpOcr;

/// <summary>
/// Per-invocation recognition options for PP-OCRv4.
/// Default accept thresholds are calibrated solely on the tuning split (MisreadPassRate &lt;= 0.05).
/// </summary>
public sealed record PpOcrRunOptions(
    PpOcrLanguage Language = PpOcrRunOptions.DefaultLanguage,
    PpOcrPipelineMode PipelineMode = PpOcrRunOptions.DefaultPipelineMode,
    PpOcrPolarityMode PolarityMode = PpOcrRunOptions.DefaultPolarityMode,
    OcrAllowedCharacterSet AllowedCharacterSet = PpOcrRunOptions.DefaultAllowedCharacterSet,
    float MinAcceptConfidence = PpOcrRunOptions.DefaultMinAcceptConfidence,
    float MinCharacterConfidence = PpOcrRunOptions.DefaultMinCharacterConfidence,
    DbDetOptions? DetOptions = null,
    bool FastInteractivePreview = PpOcrRunOptions.DefaultFastInteractivePreview,
    bool SkipOrientationFallback = PpOcrRunOptions.DefaultSkipOrientationFallback,
    int MaxBatchSize = PpOcrRunOptions.DefaultMaxBatchSize)
{
    public const PpOcrLanguage DefaultLanguage = PpOcrLanguage.MultilingualConfidenceSelect;
    public const PpOcrPipelineMode DefaultPipelineMode = PpOcrPipelineMode.Auto;
    public const PpOcrPolarityMode DefaultPolarityMode = PpOcrPolarityMode.Auto;
    public const OcrAllowedCharacterSet DefaultAllowedCharacterSet = OcrAllowedCharacterSet.All;
    public const float DefaultMinAcceptConfidence = 0.995f;
    public const float DefaultMinCharacterConfidence = 0.000f;
    public const bool DefaultFastInteractivePreview = false;
    public const bool DefaultSkipOrientationFallback = false;
    public const int DefaultMaxBatchSize = 32;
    public const int OfficialRecImageHeight = 48;
    public const int DefaultRecMinWidth = 320;
    public const int DefaultRecMaxWidth = 1280;
    public const int RecWidthBucketStep = 32;

    public static PpOcrRunOptions Default { get; } = new();

    /// <summary>
    /// Creates the high-speed production preset (<c>RecOnly</c>, single-pass polarity, exact-width batching),
    /// preserving an explicit <see cref="PpOcrLanguage.Korean"/> selection and only collapsing
    /// <see cref="PpOcrLanguage.MultilingualConfidenceSelect"/> to <see cref="PpOcrLanguage.English"/> for single-model speed.
    /// </summary>
    public static PpOcrRunOptions CreateHighSpeedProduction(
        PpOcrLanguage language = PpOcrLanguage.English,
        PpOcrPolarityMode polarityMode = PpOcrPolarityMode.Auto,
        OcrAllowedCharacterSet allowedCharacterSet = DefaultAllowedCharacterSet,
        float minAcceptConfidence = DefaultMinAcceptConfidence,
        float minCharacterConfidence = DefaultMinCharacterConfidence,
        int maxBatchSize = DefaultMaxBatchSize)
        => new(
            Language: language == PpOcrLanguage.Korean ? PpOcrLanguage.Korean : PpOcrLanguage.English,
            PipelineMode: PpOcrPipelineMode.RecOnly,
            PolarityMode: polarityMode,
            AllowedCharacterSet: allowedCharacterSet,
            MinAcceptConfidence: minAcceptConfidence,
            MinCharacterConfidence: minCharacterConfidence,
            DetOptions: null,
            FastInteractivePreview: true,
            SkipOrientationFallback: true,
            MaxBatchSize: maxBatchSize);
}
