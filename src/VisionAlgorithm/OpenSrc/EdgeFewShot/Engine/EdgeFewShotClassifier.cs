// File: src/VisionAlgorithm/OpenSrc/EdgeFewShot/Engine/EdgeFewShotClassifier.cs
using System.Runtime.CompilerServices;
using VisionAlgorithm.OpenSrc.Common;
using VisionAlgorithm.OpenSrc.EdgeFewShot.Backbone;
using VisionAlgorithm.OpenSrc.EdgeFewShot.Domain;
using VisionAlgorithm.OpenSrc.EdgeFewShot.Math;
using VisionAlgorithm.OpenSrc.EdgeFewShot.Preprocessing;

namespace VisionAlgorithm.OpenSrc.EdgeFewShot.Engine;

/// <summary>
/// Real-time zero-allocation few-shot classifier and out-of-distribution (OOD) anomaly detector.
/// Executes affine fixturing, local Z-score normalization, mid-layer GeM pooling, SimpleShot L2 projection,
/// and Cholesky-inverted Ledoit-Wolf Mahalanobis distance evaluation entirely within <see cref="ClassifierWorkspace"/> spans.
/// </summary>
public sealed class EdgeFewShotClassifier
{
    // [Math Why]: Floor on confidence denominator to avoid division by zero in softmax normalization.
    private const float SoftmaxDenominatorEpsilon = 1e-12f;

    private readonly SimdAffineRoiExtractor _roiExtractor;
    private readonly IlluminationNormalizer _illuminationNormalizer;
    private readonly IFeatureMapExtractor _featureExtractor;
    private readonly GemSpatialPooler _gemPooler;
    private readonly LedoitWolfCovariance _covarianceSolver;

    public EdgeFewShotClassifier(
        SimdAffineRoiExtractor roiExtractor,
        IlluminationNormalizer illuminationNormalizer,
        IFeatureMapExtractor featureExtractor,
        GemSpatialPooler gemPooler,
        LedoitWolfCovariance covarianceSolver)
    {
        ArgumentNullException.ThrowIfNull(roiExtractor);
        ArgumentNullException.ThrowIfNull(illuminationNormalizer);
        ArgumentNullException.ThrowIfNull(featureExtractor);
        ArgumentNullException.ThrowIfNull(gemPooler);
        ArgumentNullException.ThrowIfNull(covarianceSolver);

        _roiExtractor = roiExtractor;
        _illuminationNormalizer = illuminationNormalizer;
        _featureExtractor = featureExtractor;
        _gemPooler = gemPooler;
        _covarianceSolver = covarianceSolver;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public FewShotPredictionResult Predict(
        ReadOnlySpan<byte> sourceImage,
        int sourceWidth,
        int sourceHeight,
        int sourceStride,
        in RoiFixturePose pose,
        ClassPrototypeModel model,
        ClassifierWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceHeight);
        ArgumentOutOfRangeException.ThrowIfLessThan(sourceStride, sourceWidth);
        pose.EnsureInitialized();

        var roiWidth = model.RoiWidth;
        var roiHeight = model.RoiHeight;
        var prototypeCount = model.TotalPrototypeCount;

        workspace.EnsureCapacity(roiWidth, roiHeight, _featureExtractor, prototypeCount, model.FeatureDimension);

        _roiExtractor.Extract(
            sourceImage,
            sourceWidth,
            sourceHeight,
            sourceStride,
            in pose,
            workspace.RoiSpan,
            roiWidth,
            roiHeight);

        _illuminationNormalizer.NormalizeInPlace(workspace.RoiSpan);

        _featureExtractor.ExtractMidLayerMaps(
            workspace.RoiSpan,
            roiWidth,
            roiHeight,
            workspace.Layer2Span,
            workspace.Layer3Span,
            workspace.ScratchSpan);

        var (w2, h2) = _featureExtractor.GetLayer2Dimensions(roiWidth, roiHeight);
        var (w3, h3) = _featureExtractor.GetLayer3Dimensions(roiWidth, roiHeight);

        _gemPooler.PoolAndConcatenate(
            workspace.Layer2Span,
            _featureExtractor.Layer2Channels,
            w2,
            h2,
            workspace.Layer3Span,
            _featureExtractor.Layer3Channels,
            w3,
            h3,
            model.GemExponentP,
            workspace.RawFeatureSpan,
            workspace.ScratchSpan);

        return PredictFromRawFeature(workspace.RawFeatureSpan, model, workspace);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public FewShotPredictionResult PredictFromRawFeature(
        ReadOnlySpan<float> rawFeature,
        ClassPrototypeModel model,
        ClassifierWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(workspace);

        var dimension = model.FeatureDimension;
        if (rawFeature.Length < dimension)
        {
            throw new ArgumentException("rawFeature span is shorter than model.FeatureDimension.", nameof(rawFeature));
        }

        var prototypeCount = model.TotalPrototypeCount;
        workspace.EnsureCapacity(model.RoiWidth, model.RoiHeight, _featureExtractor, prototypeCount, dimension);

        var normalizedFeature = workspace.NormalizedFeatureSpan;
        SimpleShotNormalizer.CenterAndL2Normalize(rawFeature.Slice(0, dimension), model.GlobalMean, normalizedFeature);

        var choleskyInv = model.CholeskyInverseFactor;
        var prototypes = model.FlattenedPrototypes;
        var prototypeClasses = model.PrototypeClasses;
        var diffScratch = workspace.DifferenceScratchSpan;
        var distances = workspace.PrototypeDistancesSpan;

        var bestIndex = 0;
        var minDistance = float.MaxValue;
        var hasRivalClass = false;

        for (var p = 0; p < prototypeCount; p++)
        {
            var protoSlice = prototypes.Slice(p * dimension, dimension);
            var dist = _covarianceSolver.ComputeMahalanobisDistance(
                normalizedFeature,
                protoSlice,
                choleskyInv,
                dimension,
                diffScratch);

            distances[p] = dist;
            if (dist < minDistance)
            {
                minDistance = dist;
                bestIndex = p;
            }
        }

        var winningClass = prototypeClasses[bestIndex];
        var isOod = minDistance > model.OodMahalanobisThreshold;
        var safeThreshold = MathF.Max(model.OodMahalanobisThreshold, SoftmaxDenominatorEpsilon);

        float confidence;
        if (isOod)
        {
            // [Math Why]: When d_M > threshold, the verdict is OOD (UnknownDefect). Confidence in the OOD verdict increases monotonically in [0.5, 1.0] with excess Mahalanobis distance.
            var excessRatio = (minDistance - safeThreshold) / safeThreshold;
            confidence = System.Math.Clamp(0.5f + (0.5f * (1f - MathF.Exp(-excessRatio))), 0.5f, 1.0f);
        }
        else
        {
            // [Math Why]: Combine multi-class Gaussian posterior with radial in-distribution support so single-class models also reflect proximity to prototype within [0.5, 1.0].
            var invTempSqHalf = 0.5f / (model.TemperatureScale * model.TemperatureScale);
            var winningWeight = 0f;
            var totalWeight = 0f;

            for (var p = 0; p < prototypeCount; p++)
            {
                var shifted = distances[p] - minDistance;
                var weight = MathF.Exp(-(shifted * shifted) * invTempSqHalf);
                totalWeight += weight;
                if (prototypeClasses[p].Value == winningClass.Value)
                {
                    winningWeight += weight;
                }
                else
                {
                    hasRivalClass = true;
                }
            }

            var posteriorProbability = winningWeight / MathF.Max(totalWeight, SoftmaxDenominatorEpsilon);
            var normalizedRadius = minDistance / safeThreshold;
            var radialSupport = 1f - (0.5f * normalizedRadius * normalizedRadius);
            var combinedConfidence = hasRivalClass
                ? posteriorProbability * radialSupport
                : radialSupport;
            confidence = System.Math.Clamp(combinedConfidence, 0f, 1f);
        }

        return FewShotPredictionResult.Create(
            winningClass,
            confidence,
            minDistance,
            isOod,
            bestIndex);
    }
}
