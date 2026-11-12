using VisionAlgorithm.OpenSrc.Common;

namespace VisionAlgorithm.OpenSrc.EdgeFewShot.Engine;

/// <summary>
/// Prediction result of <see cref="EmbeddingFewShotClassifier"/> including Out-of-Distribution (OOD) anomaly detection.
/// </summary>
public readonly record struct FewShotPrediction(
    int PredictedClassLabel,
    bool IsOutOfDistribution,
    float MahalanobisDistance,
    float RejectionThreshold,
    float ConfidenceScore);
