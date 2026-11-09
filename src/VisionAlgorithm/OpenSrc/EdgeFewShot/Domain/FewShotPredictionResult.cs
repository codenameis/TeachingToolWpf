// File: src/VisionAlgorithm/OpenSrc/EdgeFewShot/Domain/FewShotPredictionResult.cs
namespace VisionAlgorithm.OpenSrc.EdgeFewShot.Domain;

/// <summary>
/// Prediction output of <see cref="Engine.EdgeFewShotClassifier"/> containing the predicted class (or <see cref="FewShotClassId.UnknownDefect"/> on OOD rejection),
/// calibrated confidence in <c>[0, 1]</c>, Mahalanobis distance, and nearest-prototype index.
/// </summary>
public readonly record struct FewShotPredictionResult
{
    private readonly bool _isValid;

    public FewShotClassId PredictedClass { get; }
    public FewShotClassId NearestInDistributionClass { get; }
    public float Confidence { get; }
    public float MahalanobisDistance { get; }
    public bool IsOutOfDistribution { get; }
    public int WinningPrototypeIndex { get; }
    public bool IsValid => _isValid;
    public bool IsEmpty => !_isValid;

    private FewShotPredictionResult(
        FewShotClassId predictedClass,
        FewShotClassId nearestInDistributionClass,
        float confidence,
        float mahalanobisDistance,
        bool isOutOfDistribution,
        int winningPrototypeIndex,
        bool isValid)
    {
        PredictedClass = predictedClass;
        NearestInDistributionClass = nearestInDistributionClass;
        Confidence = confidence;
        MahalanobisDistance = mahalanobisDistance;
        IsOutOfDistribution = isOutOfDistribution;
        WinningPrototypeIndex = winningPrototypeIndex;
        _isValid = isValid;
    }

    public static FewShotPredictionResult Empty => new(
        FewShotClassId.Empty,
        FewShotClassId.Empty,
        0f,
        0f,
        isOutOfDistribution: false,
        winningPrototypeIndex: -1,
        isValid: false);

    public static FewShotPredictionResult Create(
        FewShotClassId nearestInDistributionClass,
        float confidence,
        float mahalanobisDistance,
        bool isOutOfDistribution,
        int winningPrototypeIndex)
    {
        nearestInDistributionClass.EnsureInitialized();
        if (!float.IsFinite(confidence) || (confidence < 0f) || (confidence > 1f))
        {
            throw new ArgumentOutOfRangeException(nameof(confidence), "Confidence must be finite and within [0, 1].");
        }

        if (!float.IsFinite(mahalanobisDistance) || (mahalanobisDistance < 0f))
        {
            throw new ArgumentOutOfRangeException(nameof(mahalanobisDistance), "MahalanobisDistance must be finite and non-negative.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(winningPrototypeIndex);

        var predictedClass = isOutOfDistribution ? FewShotClassId.UnknownDefect : nearestInDistributionClass;
        return new FewShotPredictionResult(
            predictedClass,
            nearestInDistributionClass,
            confidence,
            mahalanobisDistance,
            isOutOfDistribution,
            winningPrototypeIndex,
            isValid: true);
    }
}
