// File: src/VisionAlgorithm/OpenSrc/EdgeFewShot/Domain/FewShotTrainOptions.cs
namespace VisionAlgorithm.OpenSrc.EdgeFewShot.Domain;

/// <summary>
/// Validated training configuration for GeM spatial pooling (TPAMI 2018), K-Means++ multi-prototype clustering,
/// Ledoit-Wolf shrinkage covariance (2004), and out-of-distribution (OOD) Mahalanobis gating.
/// </summary>
public readonly record struct FewShotTrainOptions
{
    // [Math Why]: Radenovic et al. (TPAMI 2018) show p = 3.0 amplifies localized high-activation defects without max-pooling noise sensitivity.
    public const float DefaultGemExponentP = 3.0f;
    public const float MinGemExponentP = 1.0f;
    public const float MaxGemExponentP = 10.0f;

    // [Math Why]: Positive shrinkage lambda in (0, 1) guarantees strict positive-definiteness of sample covariance when D >> N (Ledoit & Wolf 2004).
    public const float DefaultShrinkageLambda = 0.15f;
    public const float MinShrinkageLambda = 1e-4f;
    public const float MaxShrinkageLambda = 0.999f;

    public const int DefaultSubPrototypesPerClass = 2;
    public const int MaxSubPrototypesPerClass = 8;
    public const int MinRoiDimension = 8;
    public const int MaxRoiDimension = 1024;
    public const int DefaultRoiWidth = 64;
    public const int DefaultRoiHeight = 64;

    // [Math Why]: Multiplier over empirical max in-distribution Mahalanobis distance plus safety floor for 5-20 shot sets.
    public const float DefaultOodThresholdMultiplier = 2.5f;
    public const float MinOodThresholdMultiplier = 1.05f;
    public const int DefaultRandomSeed = 1337;

    private readonly bool _isInitialized;

    public int RoiWidth { get; }
    public int RoiHeight { get; }
    public float GemExponentP { get; }
    public float ShrinkageLambda { get; }
    public int SubPrototypesPerClass { get; }
    public float OodThresholdMultiplier { get; }
    public float ExplicitOodThreshold { get; }
    public int RandomSeed { get; }
    public bool IsInitialized => _isInitialized;
    public bool IsEmpty => !_isInitialized;

    private FewShotTrainOptions(
        int roiWidth,
        int roiHeight,
        float gemExponentP,
        float shrinkageLambda,
        int subPrototypesPerClass,
        float oodThresholdMultiplier,
        float explicitOodThreshold,
        int randomSeed,
        bool isInitialized)
    {
        RoiWidth = roiWidth;
        RoiHeight = roiHeight;
        GemExponentP = gemExponentP;
        ShrinkageLambda = shrinkageLambda;
        SubPrototypesPerClass = subPrototypesPerClass;
        OodThresholdMultiplier = oodThresholdMultiplier;
        ExplicitOodThreshold = explicitOodThreshold;
        RandomSeed = randomSeed;
        _isInitialized = isInitialized;
    }

    public static FewShotTrainOptions Empty => new(0, 0, 0f, 0f, 0, 0f, 0f, 0, isInitialized: false);

    public static FewShotTrainOptions Default => Create();

    public static FewShotTrainOptions Create(
        int roiWidth = DefaultRoiWidth,
        int roiHeight = DefaultRoiHeight,
        float gemExponentP = DefaultGemExponentP,
        float shrinkageLambda = DefaultShrinkageLambda,
        int subPrototypesPerClass = DefaultSubPrototypesPerClass,
        float oodThresholdMultiplier = DefaultOodThresholdMultiplier,
        float explicitOodThreshold = 0f,
        int randomSeed = DefaultRandomSeed)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(roiWidth, MinRoiDimension);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(roiWidth, MaxRoiDimension);
        ArgumentOutOfRangeException.ThrowIfLessThan(roiHeight, MinRoiDimension);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(roiHeight, MaxRoiDimension);

        if (!float.IsFinite(gemExponentP) || (gemExponentP < MinGemExponentP) || (gemExponentP > MaxGemExponentP))
        {
            throw new ArgumentOutOfRangeException(nameof(gemExponentP), $"GeM exponent p must be within [{MinGemExponentP}, {MaxGemExponentP}].");
        }

        if (!float.IsFinite(shrinkageLambda) || (shrinkageLambda < MinShrinkageLambda) || (shrinkageLambda > MaxShrinkageLambda))
        {
            throw new ArgumentOutOfRangeException(nameof(shrinkageLambda), $"Shrinkage lambda must be within [{MinShrinkageLambda}, {MaxShrinkageLambda}].");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(subPrototypesPerClass);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(subPrototypesPerClass, MaxSubPrototypesPerClass);

        if (!float.IsFinite(oodThresholdMultiplier) || (oodThresholdMultiplier < MinOodThresholdMultiplier))
        {
            throw new ArgumentOutOfRangeException(nameof(oodThresholdMultiplier), $"OodThresholdMultiplier must be >= {MinOodThresholdMultiplier}.");
        }

        if (!float.IsFinite(explicitOodThreshold) || (explicitOodThreshold < 0f))
        {
            throw new ArgumentOutOfRangeException(nameof(explicitOodThreshold), "ExplicitOodThreshold must be finite and non-negative.");
        }

        return new FewShotTrainOptions(
            roiWidth,
            roiHeight,
            gemExponentP,
            shrinkageLambda,
            subPrototypesPerClass,
            oodThresholdMultiplier,
            explicitOodThreshold,
            randomSeed,
            isInitialized: true);
    }

    public void EnsureInitialized()
    {
        if (!_isInitialized)
        {
            throw new InvalidOperationException("FewShotTrainOptions must be created via Create() or Default rather than default(FewShotTrainOptions).");
        }
    }
}
