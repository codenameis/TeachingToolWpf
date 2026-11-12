namespace VisionAlgorithm.OpenSrc.PatternMatching;

/// <summary>
/// Named thresholds and per-proposal execution parameters for <see cref="ContinuousVerifier"/> verification stages.
/// </summary>
internal readonly record struct VerificationThresholds(
    int Level,
    double MinimumScore,
    double FeatureUpperBound,
    double Greediness,
    bool AllowElastic,
    double MaxOcclusionRatio,
    bool StrictContext,
    bool HasElasticPatches,
    bool HasUniformGrid,
    bool HasAsymFlags,
    bool HasOccupiedContext,
    bool ElasticFeatures,
    int ElasticSpan,
    bool AllowOcclusion,
    bool HasChains,
    bool HasPartitions)
{
    public const double StrictMaxOcclusionGate = 0.2;
    public const int MaxStackFeatureScoreCount = 512;
    public const int SpatialPartitionCount = 8;
    public const int SymmetryQuadrantCount = 4;
    public const double PolarityReverseScale = 0.5;
    public const double PolarityReverseFloor = -0.5;
    public const double FeatureSupportThreshold = 0.8;
    public const double PartitionSupportThreshold = 0.78;
    public const double UniformShiftRecordThreshold = 0.75;
    public const double ElasticBypassScoreThreshold = 0.92;
    public const double ElasticStepPixels = 0.5;
    public const double UniformGridMinDirectionSquared = 0.64;
    public const double ElasticDistanceDecay = 0.02;
    public const double UnmatchedFeatureThreshold = 0.65;
    public const double OcclusionEarlyExitFactor = 0.72;
    public const double EarlyExitFloatingPointSlack = 1e-12;

    public const double UniformBlockSupportFraction = 0.65;
    public const double UniformBlockMaxShiftVariance = 0.35;
    public const double UniformMinSupportedBlocksFraction = 0.80;
    public const double UniformMinCoherentBlocksFraction = 0.75;

    public const double MinOccupiedAsymKeyThreshold = 0.72;
    public const int MinDeadBlockFeatureCount = 4;
    public const double DeadBlockMaxMeanScore = 0.22;
    public const int MinActiveBlocksForDeadGate = 4;
    public const double DeadBlockBasePenalty = 0.68;
    public const double DeadBlockPerExtraPenalty = 0.14;
    public const double DeadBlockDeficitSlope = 0.20;
    public const double DeadBlockMinPenalty = 0.42;

    public const int TrimmedMeanMinFeatureCount = 12;
    public const int TrimmedMeanDropDivisor = 5;
    public const double TrimmedMeanActivationThreshold = 0.85;
    public const double TrimmedMeanKeptWeight = 0.85;
    public const double TrimmedMeanOriginalWeight = 0.15;

    public const int MaxOccludedBlockLimit = 4;
    public const double MinInlierRatioFloor = 0.50;
    public const double OccludedBlockStrictMeanThreshold = 0.48;
    public const double OccludedBlockSoftMeanThreshold = 0.58;
    public const double OccludedBlockSoftSupportThreshold = 0.42;
    public const double ElasticInlierRequiredMean = 0.90;
    public const double RigidInlierRequiredMean = 0.84;
    public const double OccludedCountWeight = 0.18;

    public const double OuterChainStrongThreshold = 0.85;
    public const double InnerChainMissingThreshold = 0.35;
    public const double ChainPolarityWeakerSignThreshold = 0.15;
    public const double ChainPolarityMinimumStrength = 0.50;
    public const double ChainMismatchPenaltyFactor = 0.40;

    public const double StrictRotationalAsymThreshold = 0.85;
    public const double StrictAxisAsymThreshold = 0.78;
    public const double StrictQuadrantAsymThreshold = 0.65;
    public const double StrictRigidMeanThreshold = 0.88;
    public const int MaxAllowedUnmatchedAsymFeatures = 2;
    public const int MaxAllowedUnmatchedTotalFeatures = 3;
    public const double StrictViolationPenaltyScale = 0.45;
    public const double StrictViolationScoreCap = 0.50;
    public const double SingleKeyTargetThreshold = 0.55;
    public const double MultiKeyTargetThreshold = 0.78;
    public const double SingleKeyStrictFloor = 0.45;
    public const double MultiKeyStrictFloor = 0.72;
    public const double SingleKeySoftFloor = 0.40;
    public const double MultiKeySoftFloor = 0.68;
    public const double SoftFloorScoreCap = 0.54;

    public static VerificationThresholds Create(
        ShapeModel model,
        int level,
        int count,
        double minimumScore,
        double featureUpperBound,
        double greediness,
        bool allowElastic,
        double maxOcclusionRatio,
        bool strictContext,
        bool usesLocalTolerance)
    {
        var hasElasticPatches = model.FineDetail?.HasElasticPatches == true;
        var hasUniformGrid = model.FineDetail?.HasUniformGridElasticity == true;
        var hasAsymFlags = level == 0 && !model.FeatureAsymmetryFlags.IsDefault && model.FeatureAsymmetryFlags.Length == count;
        var hasOccupiedContext = hasAsymFlags && model.OccupiedContextSectorMask != 0;
        var elasticFeatures = allowElastic && level == 0 && (hasElasticPatches || hasUniformGrid || usesLocalTolerance);
        var elasticSpan = hasUniformGrid ? ((hasOccupiedContext && strictContext && maxOcclusionRatio <= StrictMaxOcclusionGate) ? 1 : 2) : 3;
        var allowOcclusion = maxOcclusionRatio > 0;
        var hasChains = level == 0 && !model.FeatureChains.IsDefault && model.FeatureChains.Length == count;
        var hasPartitions = level == 0 && !model.FeaturePartitions.IsDefault && model.FeaturePartitions.Length == count;

        return new VerificationThresholds(
            Level: level,
            MinimumScore: minimumScore,
            FeatureUpperBound: featureUpperBound,
            Greediness: greediness,
            AllowElastic: allowElastic,
            MaxOcclusionRatio: maxOcclusionRatio,
            StrictContext: strictContext,
            HasElasticPatches: hasElasticPatches,
            HasUniformGrid: hasUniformGrid,
            HasAsymFlags: hasAsymFlags,
            HasOccupiedContext: hasOccupiedContext,
            ElasticFeatures: elasticFeatures,
            ElasticSpan: elasticSpan,
            AllowOcclusion: allowOcclusion,
            HasChains: hasChains,
            HasPartitions: hasPartitions);
    }
}
