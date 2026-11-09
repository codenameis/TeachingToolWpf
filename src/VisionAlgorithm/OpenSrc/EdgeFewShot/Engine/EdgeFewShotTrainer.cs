// File: src/VisionAlgorithm/OpenSrc/EdgeFewShot/Engine/EdgeFewShotTrainer.cs
using System.Buffers;
using VisionAlgorithm.OpenSrc.EdgeFewShot.Backbone;
using VisionAlgorithm.OpenSrc.EdgeFewShot.Domain;
using VisionAlgorithm.OpenSrc.EdgeFewShot.Math;
using VisionAlgorithm.OpenSrc.EdgeFewShot.Preprocessing;

namespace VisionAlgorithm.OpenSrc.EdgeFewShot.Engine;

/// <summary>
/// Per-sample local image crop and fixture pose input for few-shot training without full-image copies or atlas packing.
/// </summary>
public readonly record struct RoiTrainingSample(
    ReadOnlyMemory<byte> Pixels,
    int Width,
    int Height,
    int Stride,
    RoiFixturePose Pose,
    FewShotClassId ClassId);

/// <summary>
/// Few-shot training engine executing Stage 1 (Affine ROI + Z-Score), Stage 2 (Mid-Layer Extraction + GeM Pooling),
/// and Stage 3 (SimpleShot Centering/L2 + K-Means++ Multi-Prototypes + Ledoit-Wolf Shrinkage Covariance Cholesky Solver).
/// </summary>
public sealed class EdgeFewShotTrainer
{
    // [Math Why]: Minimum safety margin in Mahalanobis standard-deviation units so tightly clustered 5-shot samples do not trigger false OOD on minor sensor noise.
    private const float MinOodThresholdFloor = 3.5f;
    private const float DefaultTemperatureScale = 1.0f;

    // [Math Why]: Leave-one-out peer distance includes variance from both sample i and sample j (sqrt(2) factor); scaling by 0.85 estimates single-sample to centroid deviation.
    private const float LeaveOneOutPeerToCentroidScale = 0.85f;

    private readonly SimdAffineRoiExtractor _roiExtractor;
    private readonly IlluminationNormalizer _illuminationNormalizer;
    private readonly IFeatureMapExtractor _featureExtractor;
    private readonly GemSpatialPooler _gemPooler;
    private readonly SimpleShotNormalizer _simpleShotNormalizer;
    private readonly KMeansPlusPlusClusterer _clusterer;
    private readonly LedoitWolfCovariance _covarianceEstimator;

    public EdgeFewShotTrainer(
        SimdAffineRoiExtractor roiExtractor,
        IlluminationNormalizer illuminationNormalizer,
        IFeatureMapExtractor featureExtractor,
        GemSpatialPooler gemPooler,
        SimpleShotNormalizer simpleShotNormalizer,
        KMeansPlusPlusClusterer clusterer,
        LedoitWolfCovariance covarianceEstimator)
    {
        ArgumentNullException.ThrowIfNull(roiExtractor);
        ArgumentNullException.ThrowIfNull(illuminationNormalizer);
        ArgumentNullException.ThrowIfNull(featureExtractor);
        ArgumentNullException.ThrowIfNull(gemPooler);
        ArgumentNullException.ThrowIfNull(simpleShotNormalizer);
        ArgumentNullException.ThrowIfNull(clusterer);
        ArgumentNullException.ThrowIfNull(covarianceEstimator);

        _roiExtractor = roiExtractor;
        _illuminationNormalizer = illuminationNormalizer;
        _featureExtractor = featureExtractor;
        _gemPooler = gemPooler;
        _simpleShotNormalizer = simpleShotNormalizer;
        _clusterer = clusterer;
        _covarianceEstimator = covarianceEstimator;
    }

    public ClassPrototypeModel TrainFromImages(
        IReadOnlyList<byte[]> images,
        int imageWidth,
        int imageHeight,
        int imageStride,
        ReadOnlySpan<RoiFixturePose> poses,
        ReadOnlySpan<FewShotClassId> classLabels,
        in FewShotTrainOptions options)
    {
        ArgumentNullException.ThrowIfNull(images);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(images.Count);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(imageWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(imageHeight);
        ArgumentOutOfRangeException.ThrowIfLessThan(imageStride, imageWidth);
        options.EnsureInitialized();

        var sampleCount = images.Count;
        if ((poses.Length != sampleCount) || (classLabels.Length != sampleCount))
        {
            throw new ArgumentException("Images, poses, and classLabels must all have the same length.");
        }

        for (var i = 0; i < sampleCount; i++)
        {
            ArgumentNullException.ThrowIfNull(images[i]);
            poses[i].EnsureInitialized();
            classLabels[i].EnsureInitialized();
        }

        var roiWidth = options.RoiWidth;
        var roiHeight = options.RoiHeight;
        var featureDim = GemSpatialPooler.GetPooledFeatureDimension(_featureExtractor.Layer2Channels, _featureExtractor.Layer3Channels);

        using var workspace = new ClassifierWorkspace();
        workspace.EnsureCapacity(roiWidth, roiHeight, _featureExtractor, prototypeCount: 1, featureDimension: featureDim);

        var rawFeaturesPool = ArrayPool<float>.Shared.Rent(checked(sampleCount * featureDim));
        try
        {
            var rawFeatures = rawFeaturesPool.AsSpan(0, sampleCount * featureDim);
            var (w2, h2) = _featureExtractor.GetLayer2Dimensions(roiWidth, roiHeight);
            var (w3, h3) = _featureExtractor.GetLayer3Dimensions(roiWidth, roiHeight);

            for (var s = 0; s < sampleCount; s++)
            {
                _roiExtractor.Extract(
                    images[s],
                    imageWidth,
                    imageHeight,
                    imageStride,
                    in poses[s],
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

                _gemPooler.PoolAndConcatenate(
                    workspace.Layer2Span,
                    _featureExtractor.Layer2Channels,
                    w2,
                    h2,
                    workspace.Layer3Span,
                    _featureExtractor.Layer3Channels,
                    w3,
                    h3,
                    options.GemExponentP,
                    rawFeatures.Slice(s * featureDim, featureDim),
                    workspace.ScratchSpan);
            }

            return TrainFromExtractedFeatures(rawFeatures, sampleCount, featureDim, classLabels, in options);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(rawFeaturesPool);
        }
    }

    public ClassPrototypeModel TrainFromSamples(
        IReadOnlyList<RoiTrainingSample> samples,
        in FewShotTrainOptions options)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(samples.Count);
        options.EnsureInitialized();

        var sampleCount = samples.Count;
        var classLabels = new FewShotClassId[sampleCount];
        for (var i = 0; i < sampleCount; i++)
        {
            var sample = samples[i];
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sample.Width);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sample.Height);
            ArgumentOutOfRangeException.ThrowIfLessThan(sample.Stride, sample.Width);
            if (sample.Pixels.Length < checked((sample.Height - 1) * sample.Stride + sample.Width))
            {
                throw new ArgumentException($"Sample #{i} pixel buffer is smaller than required dimensions.", nameof(samples));
            }

            sample.Pose.EnsureInitialized();
            sample.ClassId.EnsureInitialized();
            classLabels[i] = sample.ClassId;
        }

        var roiWidth = options.RoiWidth;
        var roiHeight = options.RoiHeight;
        var featureDim = GemSpatialPooler.GetPooledFeatureDimension(_featureExtractor.Layer2Channels, _featureExtractor.Layer3Channels);

        using var workspace = new ClassifierWorkspace();
        workspace.EnsureCapacity(roiWidth, roiHeight, _featureExtractor, prototypeCount: 1, featureDimension: featureDim);

        var rawFeaturesPool = ArrayPool<float>.Shared.Rent(checked(sampleCount * featureDim));
        try
        {
            var rawFeatures = rawFeaturesPool.AsSpan(0, sampleCount * featureDim);
            var (w2, h2) = _featureExtractor.GetLayer2Dimensions(roiWidth, roiHeight);
            var (w3, h3) = _featureExtractor.GetLayer3Dimensions(roiWidth, roiHeight);

            for (var s = 0; s < sampleCount; s++)
            {
                var sample = samples[s];
                var pose = sample.Pose;
                _roiExtractor.Extract(
                    sample.Pixels.Span,
                    sample.Width,
                    sample.Height,
                    sample.Stride,
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

                _gemPooler.PoolAndConcatenate(
                    workspace.Layer2Span,
                    _featureExtractor.Layer2Channels,
                    w2,
                    h2,
                    workspace.Layer3Span,
                    _featureExtractor.Layer3Channels,
                    w3,
                    h3,
                    options.GemExponentP,
                    rawFeatures.Slice(s * featureDim, featureDim),
                    workspace.ScratchSpan);
            }

            return TrainFromExtractedFeatures(rawFeatures, sampleCount, featureDim, classLabels, in options);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(rawFeaturesPool);
        }
    }

    public ClassPrototypeModel TrainFromExtractedFeatures(
        ReadOnlySpan<float> rawFeatures,
        int sampleCount,
        int featureDimension,
        ReadOnlySpan<FewShotClassId> classLabels,
        in FewShotTrainOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(featureDimension);
        options.EnsureInitialized();

        if (rawFeatures.Length < checked(sampleCount * featureDimension))
        {
            throw new ArgumentException("rawFeatures span is shorter than sampleCount * featureDimension.", nameof(rawFeatures));
        }

        if (classLabels.Length != sampleCount)
        {
            throw new ArgumentException("classLabels length must equal sampleCount.", nameof(classLabels));
        }

        var distinctClasses = new List<FewShotClassId>(8);
        for (var s = 0; s < sampleCount; s++)
        {
            classLabels[s].EnsureInitialized();
            var exists = false;
            for (var c = 0; c < distinctClasses.Count; c++)
            {
                if (distinctClasses[c].Value == classLabels[s].Value)
                {
                    exists = true;
                    break;
                }
            }

            if (!exists)
            {
                distinctClasses.Add(classLabels[s]);
            }
        }

        var globalMean = new float[featureDimension];
        _simpleShotNormalizer.ComputeGlobalMean(rawFeatures, sampleCount, featureDimension, globalMean);

        var normalizedFeatures = new float[checked(sampleCount * featureDimension)];
        for (var s = 0; s < sampleCount; s++)
        {
            _simpleShotNormalizer.CenterAndNormalizeInto(
                rawFeatures.Slice(s * featureDimension, featureDimension),
                globalMean,
                normalizedFeatures.AsSpan(s * featureDimension, featureDimension));
        }

        var prototypeClassList = new List<FewShotClassId>(distinctClasses.Count * options.SubPrototypesPerClass);
        var prototypeVectorsList = new List<float>(distinctClasses.Count * options.SubPrototypesPerClass * featureDimension);
        var residuals = new float[checked(sampleCount * featureDimension)];
        var sampleSubClusterId = new int[sampleCount];
        var sampleSubClusterCount = new int[sampleCount];
        var residualRowIndex = 0;

        for (var c = 0; c < distinctClasses.Count; c++)
        {
            var classId = distinctClasses[c];
            var classSampleIndices = new List<int>(sampleCount);
            for (var s = 0; s < sampleCount; s++)
            {
                if (classLabels[s].Value == classId.Value)
                {
                    classSampleIndices.Add(s);
                }
            }

            var classSampleCount = classSampleIndices.Count;
            var classMatrix = new float[checked(classSampleCount * featureDimension)];
            var classMean = new float[featureDimension];
            for (var idx = 0; idx < classSampleCount; idx++)
            {
                var globalSampleIdx = classSampleIndices[idx];
                var sampleSlice = normalizedFeatures.AsSpan(globalSampleIdx * featureDimension, featureDimension);
                sampleSlice.CopyTo(classMatrix.AsSpan(idx * featureDimension, featureDimension));
                for (var d = 0; d < featureDimension; d++)
                {
                    classMean[d] += sampleSlice[d];
                }
            }

            var invClassCount = 1f / classSampleCount;
            for (var d = 0; d < featureDimension; d++)
            {
                classMean[d] *= invClassCount;
            }

            // [Math Why]: Ensure at least 2 samples per sub-prototype on average (requestedK <= classSampleCount / 2) so K-Means++ does not create 1-sample clusters with zero residual variance.
            var requestedK = System.Math.Clamp(classSampleCount / 2, 1, options.SubPrototypesPerClass);
            var classCentroids = new float[checked(requestedK * featureDimension)];
            var assignments = new int[classSampleCount];

            var actualK = _clusterer.Cluster(
                classMatrix,
                classSampleCount,
                featureDimension,
                requestedK,
                options.RandomSeed + classId.Value,
                classCentroids,
                assignments);

            var clusterCounts = new int[actualK];
            for (var idx = 0; idx < classSampleCount; idx++)
            {
                clusterCounts[assignments[idx]]++;
            }

            var basePrototypeIndex = prototypeClassList.Count;
            for (var k = 0; k < actualK; k++)
            {
                prototypeClassList.Add(classId);
                var centroidSlice = classCentroids.AsSpan(k * featureDimension, featureDimension);
                for (var d = 0; d < featureDimension; d++)
                {
                    prototypeVectorsList.Add(centroidSlice[d]);
                }
            }

            for (var idx = 0; idx < classSampleCount; idx++)
            {
                var assignedCluster = assignments[idx];
                var clusterSize = clusterCounts[assignedCluster];
                var globalSampleIdx = classSampleIndices[idx];
                sampleSubClusterId[globalSampleIdx] = basePrototypeIndex + assignedCluster;
                sampleSubClusterCount[globalSampleIdx] = clusterSize;

                var sampleSpan = classMatrix.AsSpan(idx * featureDimension, featureDimension);
                // [Math Why]: If K-Means++ isolates a single sample into its own cluster, sample - centroid == 0. Fall back to classMean (or global center 0 if class has 1 sample) to preserve non-degenerate variance.
                var referenceSpan = clusterSize >= 2
                    ? classCentroids.AsSpan(assignedCluster * featureDimension, featureDimension)
                    : classMean.AsSpan();
                var resSpan = residuals.AsSpan(residualRowIndex * featureDimension, featureDimension);

                if (classSampleCount <= 1)
                {
                    sampleSpan.CopyTo(resSpan);
                }
                else
                {
                    for (var d = 0; d < featureDimension; d++)
                    {
                        resSpan[d] = sampleSpan[d] - referenceSpan[d];
                    }
                }

                residualRowIndex++;
            }
        }

        var flattenedPrototypes = prototypeVectorsList.ToArray();
        var prototypeClasses = prototypeClassList.ToArray();
        var totalPrototypes = prototypeClasses.Length;
        var effectiveDegreesOfFreedom = System.Math.Max(1, sampleCount - totalPrototypes);

        var matrixFloats = checked(featureDimension * featureDimension);
        var choleskyLower = new float[matrixFloats];
        var choleskyInverse = new float[matrixFloats];

        _covarianceEstimator.EstimateRegularizedCholeskyInverse(
            residuals,
            sampleCount,
            featureDimension,
            options.ShrinkageLambda,
            choleskyLower,
            choleskyInverse,
            effectiveDegreesOfFreedom);

        var diffScratch = new float[featureDimension];
        var maxInDistDistance = 0f;
        var sumInDistDistance = 0f;

        for (var s = 0; s < sampleCount; s++)
        {
            var sampleVec = normalizedFeatures.AsSpan(s * featureDimension, featureDimension);
            var sampleClass = classLabels[s];
            var minClassDist = float.MaxValue;

            for (var p = 0; p < totalPrototypes; p++)
            {
                if (prototypeClasses[p].Value != sampleClass.Value)
                {
                    continue;
                }

                var protoVec = flattenedPrototypes.AsSpan(p * featureDimension, featureDimension);
                var dist = _covarianceEstimator.ComputeMahalanobisDistance(
                    sampleVec,
                    protoVec,
                    choleskyInverse,
                    featureDimension,
                    diffScratch);

                if (dist < minClassDist)
                {
                    minClassDist = dist;
                }
            }

            if (minClassDist > maxInDistDistance)
            {
                maxInDistDistance = minClassDist;
            }

            sumInDistDistance += minClassDist;
        }

        // [Math Why]: In few-shot learning (N < D), training samples span an N-dimensional subspace and are pulled toward their own centroids.
        // Leave-one-out nearest-peer Mahalanobis distance within the same sub-cluster (or same class if singleton) measures intra-mode spread.
        var maxLeaveOneOutPeerDist = 0f;
        var maxSubClusterSpanDist = 0f;
        for (var i = 0; i < sampleCount; i++)
        {
            var vecI = normalizedFeatures.AsSpan(i * featureDimension, featureDimension);
            var requireSameSubCluster = sampleSubClusterCount[i] >= 2;
            var nearestPeer = float.MaxValue;
            var farthestClusterPeer = 0f;
            for (var j = 0; j < sampleCount; j++)
            {
                if ((i == j) || (classLabels[i].Value != classLabels[j].Value))
                {
                    continue;
                }

                if (requireSameSubCluster && (sampleSubClusterId[i] != sampleSubClusterId[j]))
                {
                    continue;
                }

                var vecJ = normalizedFeatures.AsSpan(j * featureDimension, featureDimension);
                var dist = _covarianceEstimator.ComputeMahalanobisDistance(vecI, vecJ, choleskyInverse, featureDimension, diffScratch);
                if (dist < nearestPeer)
                {
                    nearestPeer = dist;
                }

                if (dist > farthestClusterPeer)
                {
                    farthestClusterPeer = dist;
                }
            }

            if ((nearestPeer < float.MaxValue) && (nearestPeer > maxLeaveOneOutPeerDist))
            {
                maxLeaveOneOutPeerDist = nearestPeer;
            }

            if (farthestClusterPeer > maxSubClusterSpanDist)
            {
                maxSubClusterSpanDist = farthestClusterPeer;
            }
        }

        // [Math Why]: In high-dimensional few-shot learning (D > N_dof), training residuals lie strictly inside the N_dof-dimensional span
        // where variance is (1 - lambda) * sigma^2 + lambda * mu, whereas an unseen test sample also projects onto the (D - N_dof) orthogonal
        // complement where variance is shrunk to lambda * mu. Furthermore, a sub-cluster centroid of size n_k pulls its own members inward by
        // sqrt(n_k / (n_k - 1)) relative to an unseen test sample. Combining Bessel's correction sqrt(N / N_dof) with the finite-cluster LOO factor
        // yields an unbiased expected Mahalanobis radius for held-out in-distribution samples.
        var minClusterSize = 2.0f;
        var clusterPullCorrection = MathF.Sqrt(minClusterSize / (minClusterSize - 1.0f));
        var dofRadiusScale = MathF.Sqrt((float)sampleCount / (effectiveDegreesOfFreedom * (1.0f - options.ShrinkageLambda))) * clusterPullCorrection;
        var effectiveMaxInDist = System.Math.Max(
            maxInDistDistance,
            System.Math.Max(maxLeaveOneOutPeerDist * LeaveOneOutPeerToCentroidScale, maxSubClusterSpanDist * 0.5f)) * dofRadiusScale;
        var meanInDistDistance = sumInDistDistance / sampleCount;
        var calibratedOodThreshold = options.ExplicitOodThreshold > 0f
            ? options.ExplicitOodThreshold
            : System.Math.Max(effectiveMaxInDist * options.OodThresholdMultiplier, meanInDistDistance + MinOodThresholdFloor);

        var temperatureScale = MathF.Max(DefaultTemperatureScale, MathF.Sqrt(featureDimension));

        return new ClassPrototypeModel(
            options.RoiWidth,
            options.RoiHeight,
            featureDimension,
            options.GemExponentP,
            options.ShrinkageLambda,
            calibratedOodThreshold,
            temperatureScale,
            globalMean,
            prototypeClasses,
            flattenedPrototypes,
            choleskyInverse);
    }
}
