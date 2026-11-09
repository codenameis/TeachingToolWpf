// File: src/VisionAlgorithm/OpenSrc/EdgeFewShot/Domain/ClassPrototypeModel.cs
namespace VisionAlgorithm.OpenSrc.EdgeFewShot.Domain;

/// <summary>
/// Immutable trained few-shot model holding the SimpleShot global mean vector, multi-prototype class centroids,
/// the lower-triangular inverse Cholesky factor <c>L^-1</c> of the tied Ledoit-Wolf shrinkage covariance matrix,
/// and the calibrated out-of-distribution (OOD) Mahalanobis threshold.
/// </summary>
public sealed class ClassPrototypeModel
{
    private readonly float[] _globalMean;
    private readonly FewShotClassId[] _prototypeClasses;
    private readonly float[] _flattenedPrototypes;
    private readonly float[] _choleskyInverseFactor;

    public int RoiWidth { get; }
    public int RoiHeight { get; }
    public int FeatureDimension { get; }
    public int TotalPrototypeCount { get; }
    public float GemExponentP { get; }
    public float ShrinkageLambda { get; }
    public float OodMahalanobisThreshold { get; }
    public float TemperatureScale { get; }

    public ClassPrototypeModel(
        int roiWidth,
        int roiHeight,
        int featureDimension,
        float gemExponentP,
        float shrinkageLambda,
        float oodMahalanobisThreshold,
        float temperatureScale,
        ReadOnlySpan<float> globalMean,
        ReadOnlySpan<FewShotClassId> prototypeClasses,
        ReadOnlySpan<float> flattenedPrototypes,
        ReadOnlySpan<float> choleskyInverseFactor)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(roiWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(roiHeight);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(featureDimension);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(oodMahalanobisThreshold);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(temperatureScale);

        if (globalMean.Length != featureDimension)
        {
            throw new ArgumentException("Global mean length must equal FeatureDimension.", nameof(globalMean));
        }

        if (prototypeClasses.IsEmpty)
        {
            throw new ArgumentException("At least one class prototype is required.", nameof(prototypeClasses));
        }

        for (var i = 0; i < prototypeClasses.Length; i++)
        {
            prototypeClasses[i].EnsureInitialized();
        }

        var expectedPrototypeFloats = checked(prototypeClasses.Length * featureDimension);
        if (flattenedPrototypes.Length != expectedPrototypeFloats)
        {
            throw new ArgumentException("Flattened prototypes length must equal prototypeClasses.Length * FeatureDimension.", nameof(flattenedPrototypes));
        }

        var expectedMatrixFloats = checked(featureDimension * featureDimension);
        if (choleskyInverseFactor.Length != expectedMatrixFloats)
        {
            throw new ArgumentException("Cholesky inverse factor length must equal FeatureDimension * FeatureDimension.", nameof(choleskyInverseFactor));
        }

        RoiWidth = roiWidth;
        RoiHeight = roiHeight;
        FeatureDimension = featureDimension;
        TotalPrototypeCount = prototypeClasses.Length;
        GemExponentP = gemExponentP;
        ShrinkageLambda = shrinkageLambda;
        OodMahalanobisThreshold = oodMahalanobisThreshold;
        TemperatureScale = temperatureScale;

        _globalMean = globalMean.ToArray();
        _prototypeClasses = prototypeClasses.ToArray();
        _flattenedPrototypes = flattenedPrototypes.ToArray();
        _choleskyInverseFactor = choleskyInverseFactor.ToArray();
    }

    public ReadOnlySpan<float> GlobalMean => _globalMean;

    public ReadOnlySpan<FewShotClassId> PrototypeClasses => _prototypeClasses;

    public ReadOnlySpan<float> FlattenedPrototypes => _flattenedPrototypes;

    public ReadOnlySpan<float> CholeskyInverseFactor => _choleskyInverseFactor;

    public ReadOnlySpan<float> GetPrototypeVector(int prototypeIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(prototypeIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(prototypeIndex, TotalPrototypeCount);
        return _flattenedPrototypes.AsSpan(prototypeIndex * FeatureDimension, FeatureDimension);
    }
}
