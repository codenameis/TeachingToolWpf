// File: tests/VisionAlgorithm.Tests/EdgeFewShotTests.cs
using Microsoft.ML.OnnxRuntime;
using VisionAlgorithm.OpenSrc.EdgeFewShot.Backbone;
using VisionAlgorithm.OpenSrc.EdgeFewShot.Domain;
using VisionAlgorithm.OpenSrc.EdgeFewShot.Engine;
using VisionAlgorithm.OpenSrc.EdgeFewShot.Math;
using VisionAlgorithm.OpenSrc.EdgeFewShot.Preprocessing;

namespace VisionAlgorithm.Tests;

public sealed class EdgeFewShotTests
{
    private const int SceneWidth = 128;
    private const int SceneHeight = 96;
    private const int PaddedStride = 141; // Non-multiple-of-32 stride to verify row-stride discipline
    private const int RoiWidth = 32;
    private const int RoiHeight = 32;

    [Fact]
    public void DomainModels_DefaultStructBypassAndInvalidArguments_ThrowExpectedExceptions()
    {
        var uninitializedClassId = default(FewShotClassId);
        Assert.False(uninitializedClassId.IsInitialized);
        Assert.True(uninitializedClassId.IsEmpty);
        Assert.Throws<InvalidOperationException>(() => uninitializedClassId.EnsureInitialized());
        Assert.Throws<ArgumentOutOfRangeException>(() => FewShotClassId.Create(-2, "Invalid"));
        Assert.Throws<ArgumentException>(() => FewShotClassId.Create(0, "   "));

        var uninitializedPose = default(RoiFixturePose);
        Assert.False(uninitializedPose.IsInitialized);
        Assert.Throws<InvalidOperationException>(() => uninitializedPose.EnsureInitialized());
        Assert.Throws<InvalidOperationException>(() => uninitializedPose.TransformLocalToSource(1f, 1f));
        Assert.Throws<ArgumentOutOfRangeException>(() => RoiFixturePose.Create(10f, 10f, 0f, scale: 0f));

        var uninitializedOptions = default(FewShotTrainOptions);
        Assert.False(uninitializedOptions.IsInitialized);
        Assert.Throws<InvalidOperationException>(() => uninitializedOptions.EnsureInitialized());
        Assert.Throws<ArgumentOutOfRangeException>(() => FewShotTrainOptions.Create(gemExponentP: 0.5f));
        Assert.Throws<ArgumentOutOfRangeException>(() => FewShotTrainOptions.Create(shrinkageLambda: 0f));
        Assert.Throws<ArgumentOutOfRangeException>(() => FewShotTrainOptions.Create(shrinkageLambda: 1.0f));
    }

    [Theory]
    [InlineData(54.25f, 44.75f, 0.0f, 1.0f, 1.0f, 0.0f)]
    [InlineData(61.40f, 49.60f, 0.38f, 1.0f, 1.45f, 35.0f)]
    [InlineData(48.80f, 41.20f, -0.52f, 1.15f, 0.65f, -15.0f)]
    public void SimdAffineRoiExtractor_And_IlluminationNormalizer_RotatedScaledAndBrightnessShiftedTarget_PreservesCanonicalZScorePattern(
        float centerX,
        float centerY,
        float angleRad,
        float scale,
        float illuminationGain,
        float illuminationBias)
    {
        var referenceScene = new byte[SceneHeight * PaddedStride];
        var referencePose = RoiFixturePose.Create(64.0f, 48.0f, angleRad: 0f, scale: 1f);
        RenderSyntheticPartScene(
            referenceScene,
            SceneWidth,
            SceneHeight,
            PaddedStride,
            in referencePose,
            defectKind: SyntheticPartKind.Good,
            defectVariant: 0,
            gain: 1.0f,
            bias: 0.0f);

        var transformedScene = new byte[SceneHeight * PaddedStride];
        var transformedPose = RoiFixturePose.Create(centerX, centerY, angleRad, scale);
        RenderSyntheticPartScene(
            transformedScene,
            SceneWidth,
            SceneHeight,
            PaddedStride,
            in transformedPose,
            defectKind: SyntheticPartKind.Good,
            defectVariant: 0,
            gain: illuminationGain,
            bias: illuminationBias);

        var extractor = new SimdAffineRoiExtractor();
        var normalizer = new IlluminationNormalizer();

        var referenceRoi = new float[RoiWidth * RoiHeight];
        var transformedRoi = new float[RoiWidth * RoiHeight];

        extractor.Extract(referenceScene, SceneWidth, SceneHeight, PaddedStride, in referencePose, referenceRoi, RoiWidth, RoiHeight);
        extractor.Extract(transformedScene, SceneWidth, SceneHeight, PaddedStride, in transformedPose, transformedRoi, RoiWidth, RoiHeight);

        normalizer.NormalizeInPlace(referenceRoi);
        normalizer.NormalizeInPlace(transformedRoi);

        var (mean, stdDev) = IlluminationNormalizer.ComputeMeanAndStdDev(transformedRoi);
        Assert.InRange(MathF.Abs(mean), 0f, 1e-4f);
        Assert.InRange(stdDev, 0.99f, 1.01f);

        // Verify cosine similarity (normalized cross-correlation) between canonical and rotated/scaled/illuminated ROI
        var dot = 0f;
        for (var i = 0; i < referenceRoi.Length; i++)
        {
            dot += referenceRoi[i] * transformedRoi[i];
        }

        var ncc = dot / referenceRoi.Length;
        Assert.InRange(ncc, 0.94f, 1.001f);
    }

    [Fact]
    public void SimdAffineRoiExtractor_Identity64x64Pose_MatchesSourcePixelsWithZeroError()
    {
        const int size = 64;
        var source = new byte[size * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                source[(y * size) + x] = (byte)(((x * 37) + (y * 19) + (x ^ y)) & 0xFF);
            }
        }

        var pose = RoiFixturePose.Create(centerX: 32.0f, centerY: 32.0f, angleRad: 0f, scale: 1.0f);
        var destinationRoi = new float[size * size];
        var extractor = new SimdAffineRoiExtractor();

        extractor.Extract(source, size, size, size, in pose, destinationRoi, size, size);

        for (var i = 0; i < source.Length; i++)
        {
            Assert.Equal((float)source[i], destinationRoi[i], precision: 6);
        }
    }

    [Fact]
    public void SimdAffineRoiExtractor_AnisotropicScalePose_SamplesRectangularRoiWithoutClipping()
    {
        const int srcW = 128;
        const int srcH = 64;
        var source = new byte[srcW * srcH];
        for (var y = 0; y < srcH; y++)
        {
            for (var x = 0; x < srcW; x++)
            {
                source[(y * srcW) + x] = (byte)(((x * 2) + y) & 0xFF);
            }
        }

        // Map a 96 x 32 rectangular region centered at (64, 32) onto a 48 x 32 canonical ROI using ScaleX = 2.0, ScaleY = 1.0
        var pose = RoiFixturePose.CreateAnisotropic(centerX: 64.0f, centerY: 32.0f, angleRad: 0f, scaleX: 2.0f, scaleY: 1.0f);
        Assert.Equal(2.0f, pose.ScaleX);
        Assert.Equal(1.0f, pose.ScaleY);

        var dst = new float[48 * 32];
        var extractor = new SimdAffineRoiExtractor();
        extractor.Extract(source, srcW, srcH, srcW, in pose, dst, 48, 32);

        // u = 0 maps to localDx = 0.5 - 24 = -23.5 -> sourceX = 64 + (-23.5 * 2.0) = 17.0 -> index sx = 16.5
        var expectedFirst = 0.5f * (source[(16 * srcW) + 16] + source[(16 * srcW) + 17]);
        Assert.InRange(MathF.Abs(dst[0] - expectedFirst), 0f, 1e-4f);
    }

    [Fact]
    public void EdgeFewShotTrainer_And_Classifier_2SamplesPerClassWithSubPrototypes2_AvoidsZeroResidualCollapseAndMaintainsValidOodConfidence()
    {
        const int dimension = 32;
        const int sampleCount = 4; // 2 samples per class, SubPrototypesPerClass = 2
        var rawFeatures = new float[sampleCount * dimension];
        var labels = new FewShotClassId[]
        {
            FewShotClassId.Create(0, "Good"),
            FewShotClassId.Create(0, "Good"),
            FewShotClassId.Create(1, "BackBad"),
            FewShotClassId.Create(1, "BackBad")
        };

        for (var s = 0; s < sampleCount; s++)
        {
            var isBad = s >= 2;
            var jitter = (s & 1) == 0 ? -0.12f : 0.12f;
            for (var d = 0; d < dimension; d++)
            {
                rawFeatures[(s * dimension) + d] = (isBad ? MathF.Cos(d * 0.4f) : MathF.Sin(d * 0.4f)) + jitter + (d * 0.05f);
            }
        }

        var trainer = new EdgeFewShotTrainer(
            new SimdAffineRoiExtractor(),
            new IlluminationNormalizer(),
            new MultiScaleGaborHogExtractor(),
            new GemSpatialPooler(),
            new SimpleShotNormalizer(),
            new KMeansPlusPlusClusterer(),
            new LedoitWolfCovariance());
        var classifier = new EdgeFewShotClassifier(
            new SimdAffineRoiExtractor(),
            new IlluminationNormalizer(),
            new MultiScaleGaborHogExtractor(),
            new GemSpatialPooler(),
            new SimpleShotNormalizer(),
            new LedoitWolfCovariance());

        var options = FewShotTrainOptions.Create(
            roiWidth: 32,
            roiHeight: 32,
            subPrototypesPerClass: 2,
            shrinkageLambda: 0.15f);

        var model = trainer.TrainFromExtractedFeatures(rawFeatures, sampleCount, dimension, labels, in options);
        // With 2 samples per class, requestedK is clamped to 1 so singleton zero-residual clusters do not form.
        Assert.Equal(2, model.TotalPrototypeCount);
        Assert.InRange(model.OodMahalanobisThreshold, 3.0f, 40.0f);

        using var workspace = new ClassifierWorkspace();
        var inDistPred = classifier.PredictFromRawFeature(rawFeatures.AsSpan(0, dimension), model, workspace);
        Assert.False(inDistPred.IsOutOfDistribution);
        Assert.Equal(0, inDistPred.PredictedClass.Value);
        Assert.InRange(inDistPred.Confidence, 0.5f, 1.0f);

        // Severe OOD feature vector
        var oodFeature = new float[dimension];
        for (var d = 0; d < dimension; d++)
        {
            oodFeature[d] = ((d & 1) == 0 ? 15.0f : -15.0f) * (d + 1);
        }

        var oodPred = classifier.PredictFromRawFeature(oodFeature, model, workspace);
        Assert.True(oodPred.IsOutOfDistribution);
        Assert.Equal(FewShotClassId.UnknownDefect.Value, oodPred.PredictedClass.Value);
        Assert.InRange(oodPred.Confidence, 0.5f, 1.0f);
    }

    [Fact]
    public void IlluminationNormalizer_FlatConstantRegion_ReturnsAllZerosWithoutNaNOrInfinity()
    {
        var normalizer = new IlluminationNormalizer();
        var flatRoi = new float[37]; // Non-multiple of 8/16 to exercise SIMD + scalar tail
        Array.Fill(flatRoi, 128.0f);

        normalizer.NormalizeInPlace(flatRoi);

        for (var i = 0; i < flatRoi.Length; i++)
        {
            Assert.True(float.IsFinite(flatRoi[i]));
            Assert.Equal(0f, flatRoi[i], precision: 5);
        }
    }

    [Fact]
    public void MathPipeline_SimpleShot_LedoitWolfCholesky_And_KMeansPlusPlus_SatisfyMathematicalInvariants()
    {
        const int sampleCount = 8; // N = 8 < D = 32 (Singular sample covariance regime)
        const int dimension = 32;
        var rawFeatures = new float[sampleCount * dimension];

        for (var s = 0; s < sampleCount; s++)
        {
            var isModeB = s >= (sampleCount / 2);
            for (var d = 0; d < dimension; d++)
            {
                var baseSignal = isModeB ? MathF.Cos((d + 1) * 0.35f) * 3.0f : MathF.Sin((d + 1) * 0.25f) * 3.0f;
                var perturbation = ((s * 7 + d * 13) % 11) * 0.04f;
                rawFeatures[(s * dimension) + d] = baseSignal + perturbation + 5.0f;
            }
        }

        var simpleShot = new SimpleShotNormalizer();
        var globalMean = new float[dimension];
        simpleShot.ComputeGlobalMean(rawFeatures, sampleCount, dimension, globalMean);

        var centeredNormalized = new float[sampleCount * dimension];
        for (var s = 0; s < sampleCount; s++)
        {
            var outSlice = centeredNormalized.AsSpan(s * dimension, dimension);
            simpleShot.CenterAndNormalizeInto(rawFeatures.AsSpan(s * dimension, dimension), globalMean, outSlice);

            var normSq = SimpleShotNormalizer.ComputeSquaredL2Norm(outSlice);
            Assert.InRange(MathF.Sqrt(normSq), 0.9999f, 1.0001f);
        }

        var clusterer = new KMeansPlusPlusClusterer();
        var centroids = new float[2 * dimension];
        var assignments = new int[sampleCount];
        var actualK = clusterer.Cluster(centeredNormalized, sampleCount, dimension, requestedClusters: 2, seed: 42, centroids, assignments);

        Assert.Equal(2, actualK);
        // First 4 samples belong to one mode, last 4 samples belong to the second mode
        Assert.Equal(assignments[0], assignments[1]);
        Assert.Equal(assignments[0], assignments[2]);
        Assert.Equal(assignments[0], assignments[3]);
        Assert.Equal(assignments[4], assignments[5]);
        Assert.NotEqual(assignments[0], assignments[4]);

        var residuals = new float[sampleCount * dimension];
        for (var s = 0; s < sampleCount; s++)
        {
            var k = assignments[s];
            for (var d = 0; d < dimension; d++)
            {
                residuals[(s * dimension) + d] = centeredNormalized[(s * dimension) + d] - centroids[(k * dimension) + d];
            }
        }

        var ledoitWolf = new LedoitWolfCovariance();
        var lower = new float[dimension * dimension];
        var lowerInv = new float[dimension * dimension];
        ledoitWolf.EstimateRegularizedCholeskyInverse(residuals, sampleCount, dimension, shrinkageLambda: 0.15f, lower, lowerInv);

        // Invariant 1: All Cholesky diagonal elements L_ii > 0
        for (var i = 0; i < dimension; i++)
        {
            Assert.True(lower[(i * dimension) + i] > 0f);
            Assert.True(lowerInv[(i * dimension) + i] > 0f);
        }

        // Invariant 2: L^-1 * L == I_D
        for (var i = 0; i < dimension; i++)
        {
            for (var j = 0; j < dimension; j++)
            {
                var dot = 0f;
                for (var k = 0; k < dimension; k++)
                {
                    dot += lowerInv[(i * dimension) + k] * lower[(k * dimension) + j];
                }

                var expected = i == j ? 1f : 0f;
                Assert.InRange(MathF.Abs(dot - expected), 0f, 1e-4f);
            }
        }

        // Invariant 3: d_M(mu, mu) == 0 and d_M(x, y) == d_M(y, x) > 0 for x != y
        var scratch = new float[dimension];
        var c0 = centroids.AsSpan(0, dimension);
        var c1 = centroids.AsSpan(dimension, dimension);

        var selfDist = ledoitWolf.ComputeMahalanobisDistance(c0, c0, lowerInv, dimension, scratch);
        Assert.Equal(0f, selfDist, precision: 6);

        var dist01 = ledoitWolf.ComputeMahalanobisDistance(c0, c1, lowerInv, dimension, scratch);
        var dist10 = ledoitWolf.ComputeMahalanobisDistance(c1, c0, lowerInv, dimension, scratch);
        Assert.True(dist01 > 0f);
        Assert.InRange(MathF.Abs(dist01 - dist10), 0f, 1e-5f);
    }

    [Fact]
    public void GemSpatialPooler_ExponentOneMatchesArithmeticMean_And_HigherExponentAmplifiesLocalizedPeak()
    {
        var pooler = new GemSpatialPooler();
        var plane = new float[64];
        for (var i = 0; i < plane.Length; i++)
        {
            plane[i] = 0.10f;
        }

        // Inject a localized 10% defect peak
        for (var i = 0; i < 6; i++)
        {
            plane[i] = 2.50f;
        }

        var expectedGap = plane.Average();
        var gemP1 = pooler.PoolChannelPlane(plane, exponentP: 1.0f);
        var gemP3 = pooler.PoolChannelPlane(plane, exponentP: 3.0f);
        var gemP5 = pooler.PoolChannelPlane(plane, exponentP: 5.0f);

        Assert.InRange(MathF.Abs(gemP1 - expectedGap), 0f, 1e-5f);
        Assert.True(gemP3 > gemP1);
        Assert.True(gemP5 > gemP3);
        Assert.True(gemP5 <= 2.50f);
    }

    [Fact]
    public void OnnxFeatureMapExtractor_LoadedWithRepositoryResNetModel_ExtractsMidLayerFeatureMaps()
    {
        var modelPath = FindRepoOnnxModelPath();
        if (modelPath is null)
        {
            return;
        }

        using var session = new InferenceSession(modelPath);
        var inputMeta = session.InputMetadata.First();
        var outputNames = session.OutputMetadata.Keys.ToArray();
        var out0Name = outputNames[0];
        var out1Name = outputNames.Length > 1 ? outputNames[1] : outputNames[0];

        using var extractor = new OnnxFeatureMapExtractor(
            session,
            ownsSession: false,
            inputNodeName: inputMeta.Key,
            layer2OutputName: out0Name,
            layer3OutputName: out1Name,
            layer2Channels: 4,
            layer3Channels: 4,
            inputChannels: 3,
            layer2Stride: 8,
            layer3Stride: 8);

        Assert.Equal(8, extractor.TotalConcatenatedChannels);
        var (w2, h2) = extractor.GetLayer2Dimensions(64, 64);
        Assert.Equal(8, w2);
        Assert.Equal(8, h2);
    }

    [Fact]
    public void MultiScaleGaborHog_And_GemSpatialPooler_TopDarkVoidVersusBottomDarkVoid_ProducesDistinctQuadrantFeatures()
    {
        const int roiSize = 64;
        var normalizer = new IlluminationNormalizer();
        var extractor = new MultiScaleGaborHogExtractor();
        var pooler = new GemSpatialPooler();
        var (w2, h2) = extractor.GetLayer2Dimensions(roiSize, roiSize);
        var (w3, h3) = extractor.GetLayer3Dimensions(roiSize, roiSize);
        var featureDim = GemSpatialPooler.GetPooledFeatureDimension(extractor.Layer2Channels, extractor.Layer3Channels);

        var topDarkRoi = new float[roiSize * roiSize];
        var bottomDarkRoi = new float[roiSize * roiSize];
        for (var y = 0; y < roiSize; y++)
        {
            for (var x = 0; x < roiSize; x++)
            {
                var baseVal = 150.0f + ((x & 1) * 4.0f) + ((y & 1) * 4.0f);
                topDarkRoi[(y * roiSize) + x] = baseVal;
                bottomDarkRoi[(y * roiSize) + x] = baseVal;
            }
        }

        // Inject dark void defect (I << mean) in top-left quadrant vs bottom-left quadrant
        for (var y = 8; y < 20; y++)
        {
            for (var x = 8; x < 20; x++)
            {
                topDarkRoi[(y * roiSize) + x] = 15.0f;
                bottomDarkRoi[((roiSize - 1 - y) * roiSize) + x] = 15.0f;
            }
        }

        normalizer.NormalizeInPlace(topDarkRoi);
        normalizer.NormalizeInPlace(bottomDarkRoi);

        var l2 = new float[extractor.Layer2Channels * w2 * h2];
        var l3 = new float[extractor.Layer3Channels * w3 * h3];
        var scratch = new float[extractor.GetRequiredScratchFloats(roiSize, roiSize)];
        var featTop = new float[featureDim];
        var featBottom = new float[featureDim];

        extractor.ExtractMidLayerMaps(topDarkRoi, roiSize, roiSize, l2, l3, scratch);
        pooler.PoolAndConcatenate(l2, extractor.Layer2Channels, w2, h2, l3, extractor.Layer3Channels, w3, h3, exponentP: 3.0f, featTop, scratch);

        extractor.ExtractMidLayerMaps(bottomDarkRoi, roiSize, roiSize, l2, l3, scratch);
        pooler.PoolAndConcatenate(l2, extractor.Layer2Channels, w2, h2, l3, extractor.Layer3Channels, w3, h3, exponentP: 3.0f, featBottom, scratch);

        // Quadrant bins at indices [32..35] must distinguish top-left dark void (bin 32) from bottom-left dark void (bin 34)
        Assert.True(featTop[32] > featTop[34] + 0.1f);
        Assert.True(featBottom[34] > featBottom[32] + 0.1f);
    }

    [Fact]
    public void EdgeFewShotClassifier_EndToEnd5ShotTrainingAndInference_ClassifiesGoodBackBadAndRejectsUnknownAnomalyWithZeroHeapAllocation()
    {
        var goodClass = FewShotClassId.Create(0, "Good");
        var backBadClass = FewShotClassId.Create(1, "BackBad");

        const int shotsPerClass = 5;
        const int totalTrainSamples = shotsPerClass * 2;

        var trainImages = new List<byte[]>(totalTrainSamples);
        var trainPoses = new RoiFixturePose[totalTrainSamples];
        var trainLabels = new FewShotClassId[totalTrainSamples];

        // 5-shot Good samples across varying positions, rotations, and brightness levels
        for (var i = 0; i < shotsPerClass; i++)
        {
            var img = new byte[SceneHeight * PaddedStride];
            var pose = RoiFixturePose.Create(
                centerX: 62.0f + (i * 0.7f),
                centerY: 46.0f + (i * 0.5f),
                angleRad: (i - 2) * 0.04f,
                scale: 1.0f);

            RenderSyntheticPartScene(
                img,
                SceneWidth,
                SceneHeight,
                PaddedStride,
                in pose,
                SyntheticPartKind.Good,
                defectVariant: i % 2,
                gain: 0.90f + (i * 0.05f),
                bias: (i - 2) * 4.0f);

            trainImages.Add(img);
            trainPoses[i] = pose;
            trainLabels[i] = goodClass;
        }

        // 5-shot BackBad (multi-modal drip defect: top drip mode + bottom drip mode)
        for (var i = 0; i < shotsPerClass; i++)
        {
            var img = new byte[SceneHeight * PaddedStride];
            var pose = RoiFixturePose.Create(
                centerX: 63.0f - (i * 0.6f),
                centerY: 47.0f + (i * 0.4f),
                angleRad: (2 - i) * 0.04f,
                scale: 1.0f);

            RenderSyntheticPartScene(
                img,
                SceneWidth,
                SceneHeight,
                PaddedStride,
                in pose,
                SyntheticPartKind.BackBadDrip,
                defectVariant: i % 2,
                gain: 0.92f + (i * 0.04f),
                bias: (i - 2) * 3.0f);

            trainImages.Add(img);
            trainPoses[shotsPerClass + i] = pose;
            trainLabels[shotsPerClass + i] = backBadClass;
        }

        var roiExtractor = new SimdAffineRoiExtractor();
        var normalizer = new IlluminationNormalizer();
        var backbone = new MultiScaleGaborHogExtractor();
        var gemPooler = new GemSpatialPooler();
        var simpleShot = new SimpleShotNormalizer();
        var clusterer = new KMeansPlusPlusClusterer();
        var covariance = new LedoitWolfCovariance();

        var trainer = new EdgeFewShotTrainer(roiExtractor, normalizer, backbone, gemPooler, simpleShot, clusterer, covariance);
        var classifier = new EdgeFewShotClassifier(roiExtractor, normalizer, backbone, gemPooler, simpleShot, covariance);

        var options = FewShotTrainOptions.Create(
            roiWidth: RoiWidth,
            roiHeight: RoiHeight,
            gemExponentP: 3.0f,
            shrinkageLambda: 0.15f,
            subPrototypesPerClass: 2,
            oodThresholdMultiplier: 2.4f,
            randomSeed: 1337);

        var model = trainer.TrainFromImages(trainImages, SceneWidth, SceneHeight, PaddedStride, trainPoses, trainLabels, in options);
        Assert.Equal(4, model.TotalPrototypeCount);

        using var workspace = new ClassifierWorkspace();

        // 1. Test unseen Good part with novel pose and illumination
        var testGoodImage = new byte[SceneHeight * PaddedStride];
        var testGoodPose = RoiFixturePose.Create(64.35f, 48.25f, angleRad: 0.06f, scale: 1.0f);
        RenderSyntheticPartScene(testGoodImage, SceneWidth, SceneHeight, PaddedStride, in testGoodPose, SyntheticPartKind.Good, defectVariant: 0, gain: 1.12f, bias: 8.0f);

        // 2. Test unseen BackBad drip part (both variant 0 and variant 1)
        var testBadImage = new byte[SceneHeight * PaddedStride];
        var testBadPose = RoiFixturePose.Create(61.75f, 49.10f, angleRad: -0.05f, scale: 1.0f);
        RenderSyntheticPartScene(testBadImage, SceneWidth, SceneHeight, PaddedStride, in testBadPose, SyntheticPartKind.BackBadDrip, defectVariant: 1, gain: 0.85f, bias: -6.0f);

        // 3. Test unseen OOD UnknownAnomaly (severe checkerboard scratch contamination never seen during training)
        var testOodImage = new byte[SceneHeight * PaddedStride];
        var testOodPose = RoiFixturePose.Create(63.50f, 47.80f, angleRad: 0.02f, scale: 1.0f);
        RenderSyntheticPartScene(testOodImage, SceneWidth, SceneHeight, PaddedStride, in testOodPose, SyntheticPartKind.UnknownSevereContamination, defectVariant: 0, gain: 1.0f, bias: 0.0f);

        // Warmup for JIT and workspace pooling
        for (var w = 0; w < 3; w++)
        {
            _ = classifier.Predict(testGoodImage, SceneWidth, SceneHeight, PaddedStride, in testGoodPose, model, workspace);
        }

        // Verify 0-byte allocation on steady-state Predict()
        var allocBefore = GC.GetAllocatedBytesForCurrentThread();
        var goodPred = classifier.Predict(testGoodImage, SceneWidth, SceneHeight, PaddedStride, in testGoodPose, model, workspace);
        var badPred = classifier.Predict(testBadImage, SceneWidth, SceneHeight, PaddedStride, in testBadPose, model, workspace);
        var oodPred = classifier.Predict(testOodImage, SceneWidth, SceneHeight, PaddedStride, in testOodPose, model, workspace);
        var allocAfter = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0L, allocAfter - allocBefore);

        Assert.True(goodPred.IsValid);
        Assert.False(goodPred.IsOutOfDistribution);
        Assert.Equal(goodClass.Value, goodPred.PredictedClass.Value);
        Assert.InRange(goodPred.Confidence, 0.50f, 1.0f);

        Assert.True(badPred.IsValid);
        Assert.False(badPred.IsOutOfDistribution);
        Assert.Equal(backBadClass.Value, badPred.PredictedClass.Value);
        Assert.InRange(badPred.Confidence, 0.50f, 1.0f);

        Assert.True(oodPred.IsValid);
        Assert.True(oodPred.IsOutOfDistribution);
        Assert.Equal(FewShotClassId.UnknownDefect.Value, oodPred.PredictedClass.Value);
        Assert.True(oodPred.MahalanobisDistance > model.OodMahalanobisThreshold);
    }

    [Fact]
    public void EdgeFewShotBenchmark_2048x2048Scene_5ShotTrainAndPredict_CompletesInSubMillisecondWithZeroAllocation()
    {
        var roiExtractor = new SimdAffineRoiExtractor();
        var normalizer = new IlluminationNormalizer();
        var backbone = new MultiScaleGaborHogExtractor();
        var gemPooler = new GemSpatialPooler();
        var simpleShot = new SimpleShotNormalizer();
        var clusterer = new KMeansPlusPlusClusterer();
        var covariance = new LedoitWolfCovariance();

        var trainer = new EdgeFewShotTrainer(roiExtractor, normalizer, backbone, gemPooler, simpleShot, clusterer, covariance);
        var classifier = new EdgeFewShotClassifier(roiExtractor, normalizer, backbone, gemPooler, simpleShot, covariance);

        var goodClass = FewShotClassId.Create(0, "Good");
        var badClass = FewShotClassId.Create(1, "BackBad");
        const int shots = 5;
        const int total = shots * 2;

        var trainImages = new List<byte[]>(total);
        var trainPoses = new RoiFixturePose[total];
        var trainLabels = new FewShotClassId[total];

        for (var i = 0; i < shots; i++)
        {
            var img = new byte[SceneHeight * PaddedStride];
            var pose = RoiFixturePose.Create(64f, 48f, (i - 2) * 0.03f, 1.0f);
            RenderSyntheticPartScene(img, SceneWidth, SceneHeight, PaddedStride, in pose, SyntheticPartKind.Good, 0, 1.0f, 0f);
            trainImages.Add(img);
            trainPoses[i] = pose;
            trainLabels[i] = goodClass;
        }

        for (var i = 0; i < shots; i++)
        {
            var img = new byte[SceneHeight * PaddedStride];
            var pose = RoiFixturePose.Create(64f, 48f, (2 - i) * 0.03f, 1.0f);
            RenderSyntheticPartScene(img, SceneWidth, SceneHeight, PaddedStride, in pose, SyntheticPartKind.BackBadDrip, i % 2, 1.0f, 0f);
            trainImages.Add(img);
            trainPoses[shots + i] = pose;
            trainLabels[shots + i] = badClass;
        }

        var options = FewShotTrainOptions.Create(roiWidth: 64, roiHeight: 64, gemExponentP: 3.0f, shrinkageLambda: 0.15f, subPrototypesPerClass: 2);
        var trainSw = System.Diagnostics.Stopwatch.StartNew();
        var model = trainer.TrainFromImages(trainImages, SceneWidth, SceneHeight, PaddedStride, trainPoses, trainLabels, in options);
        trainSw.Stop();

        const int largeW = 2048;
        const int largeH = 2048;
        var largeScene = new byte[largeW * largeH];
        var runtimePose = RoiFixturePose.Create(1024.35f, 1024.65f, 0.08f, 1.0f);
        using var workspace = new ClassifierWorkspace();

        for (var w = 0; w < 10; w++)
        {
            _ = classifier.Predict(largeScene, largeW, largeH, largeW, in runtimePose, model, workspace);
        }

        const int iterations = 100;
        var allocBefore = GC.GetAllocatedBytesForCurrentThread();
        var predSw = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++)
        {
            _ = classifier.Predict(largeScene, largeW, largeH, largeW, in runtimePose, model, workspace);
        }

        predSw.Stop();
        var allocAfter = GC.GetAllocatedBytesForCurrentThread();
        var meanPredictUs = (predSw.Elapsed.TotalMilliseconds * 1000.0) / iterations;
        var allocatedBytes = (allocAfter - allocBefore) / iterations;

        Console.WriteLine($"[EdgeFewShot 2048x2048 Benchmark] 10-Sample Train={trainSw.Elapsed.TotalMilliseconds:F3} ms | Mean Predict={meanPredictUs:F2} us ({meanPredictUs / 1000.0:F4} ms) | Allocated={allocatedBytes} B/op");
        Assert.Equal(0L, allocatedBytes);
        Assert.True(trainSw.Elapsed.TotalMilliseconds < 50.0);
        Assert.True(meanPredictUs < 2000.0);
    }

    [Fact]
    public void EdgeFewShotClassifier_GenerateAndVerifyPngTestDataset_SavesPngFilesAndVerifiesEndToEndPredictions()
    {
        var repoRoot = FindRepositoryRoot();
        var outputDir = Path.Combine(repoRoot, "samples", "EdgeFewShot.Data");
        Directory.CreateDirectory(outputDir);

        const int cellW = 160;
        const int cellH = 140;
        const int roiSize = 64;
        var goodClass = FewShotClassId.Create(0, "Good");
        var backBadClass = FewShotClassId.Create(1, "BackBad");

        var trainImages = new List<byte[]>(10);
        var trainPoses = new RoiFixturePose[10];
        var trainLabels = new FewShotClassId[10];

        // 1. Generate 5 Good training PNGs
        for (var i = 0; i < 5; i++)
        {
            var img = new byte[cellW * cellH];
            var pose = RoiFixturePose.Create(
                centerX: (cellW * 0.5f) + ((i - 2) * 1.5f),
                centerY: (cellH * 0.5f) + ((i - 2) * 1.0f),
                angleRad: (i - 2) * 0.045f,
                scale: 1.0f);

            RenderHighResolutionComponent(
                img,
                cellW,
                cellH,
                cellW,
                in pose,
                SyntheticPartKind.Good,
                defectVariant: i % 2,
                gain: 0.90f + (i * 0.05f),
                bias: (i - 2) * 5.0f);

            SaveGrayscalePng(Path.Combine(outputDir, $"train-good-0{i + 1}.png"), img, cellW, cellH);
            trainImages.Add(img);
            trainPoses[i] = pose;
            trainLabels[i] = goodClass;
        }

        // 2. Generate 5 BackBad (multi-modal solder bridge / adhesive drip) training PNGs
        for (var i = 0; i < 5; i++)
        {
            var img = new byte[cellW * cellH];
            var pose = RoiFixturePose.Create(
                centerX: (cellW * 0.5f) - ((i - 2) * 1.2f),
                centerY: (cellH * 0.5f) + ((i - 2) * 1.1f),
                angleRad: (2 - i) * 0.04f,
                scale: 1.0f);

            RenderHighResolutionComponent(
                img,
                cellW,
                cellH,
                cellW,
                in pose,
                SyntheticPartKind.BackBadDrip,
                defectVariant: i % 2,
                gain: 0.92f + (i * 0.04f),
                bias: (i - 2) * 4.0f);

            SaveGrayscalePng(Path.Combine(outputDir, $"train-backbad-0{i + 1}.png"), img, cellW, cellH);
            trainImages.Add(img);
            trainPoses[5 + i] = pose;
            trainLabels[5 + i] = backBadClass;
        }

        // 3. Generate 4 Test PNGs (Rotated/Bright Good, Top-Right Drip, Bottom-Left Drip, OOD Severe Contamination)
        var testSpecs = new (string FileName, string Title, RoiFixturePose Pose, SyntheticPartKind Kind, int Variant, float Gain, float Bias, int ExpectedClassValue, bool ExpectOod)[]
        {
            ("test-good-rotated.png", "Test 1: Good (+4.6 deg, Bright)", RoiFixturePose.Create(82.5f, 71.5f, 0.08f, 1.0f), SyntheticPartKind.Good, 0, 1.15f, 10.0f, goodClass.Value, false),
            ("test-backbad-drip-top.png", "Test 2: BackBad (Mode A Top Drip)", RoiFixturePose.Create(77.5f, 68.5f, -0.06f, 1.0f), SyntheticPartKind.BackBadDrip, 0, 0.88f, -6.0f, backBadClass.Value, false),
            ("test-backbad-drip-bottom.png", "Test 3: BackBad (Mode B Bottom Drip)", RoiFixturePose.Create(81.0f, 72.0f, 0.05f, 1.0f), SyntheticPartKind.BackBadDrip, 1, 1.05f, 4.0f, backBadClass.Value, false),
            ("test-unknown-anomaly.png", "Test 4: UnknownDefect (OOD Splatter)", RoiFixturePose.Create(79.5f, 69.5f, -0.03f, 1.0f), SyntheticPartKind.UnknownSevereContamination, 0, 1.0f, 0.0f, FewShotClassId.UnknownDefect.Value, true)
        };

        foreach (var spec in testSpecs)
        {
            var img = new byte[cellW * cellH];
            RenderHighResolutionComponent(img, cellW, cellH, cellW, in spec.Pose, spec.Kind, spec.Variant, spec.Gain, spec.Bias);
            SaveGrayscalePng(Path.Combine(outputDir, spec.FileName), img, cellW, cellH);
        }

        // 4. Train model from reloaded PNG files on disk and run inference on the 4 test PNG files
        var roiExtractor = new SimdAffineRoiExtractor();
        var normalizer = new IlluminationNormalizer();
        var backbone = new MultiScaleGaborHogExtractor();
        var gemPooler = new GemSpatialPooler();
        var simpleShot = new SimpleShotNormalizer();
        var clusterer = new KMeansPlusPlusClusterer();
        var covariance = new LedoitWolfCovariance();

        var trainer = new EdgeFewShotTrainer(roiExtractor, normalizer, backbone, gemPooler, simpleShot, clusterer, covariance);
        var classifier = new EdgeFewShotClassifier(roiExtractor, normalizer, backbone, gemPooler, simpleShot, covariance);

        var options = FewShotTrainOptions.Create(
            roiWidth: roiSize,
            roiHeight: roiSize,
            gemExponentP: 3.0f,
            shrinkageLambda: 0.15f,
            subPrototypesPerClass: 2,
            oodThresholdMultiplier: 2.4f,
            randomSeed: 1337);

        var model = trainer.TrainFromImages(trainImages, cellW, cellH, cellW, trainPoses, trainLabels, in options);
        using var workspace = new ClassifierWorkspace();

        // 5. Render composite color overview sheet PNG (edge-fewshot-test-sheet.png) with ROI bounding boxes and prediction labels
        const int sheetW = 920;
        const int sheetH = 540;
        using var sheetMat = new OpenCvSharp.Mat(sheetH, sheetW, OpenCvSharp.MatType.CV_8UC3, new OpenCvSharp.Scalar(24, 28, 34));

        OpenCvSharp.Cv2.PutText(
            sheetMat,
            "EdgeFewShotClassifier - 5-Shot Training & Zero-Allocation Inference Test Sheet",
            new OpenCvSharp.Point(20, 32),
            OpenCvSharp.HersheyFonts.HersheySimplex,
            0.62,
            new OpenCvSharp.Scalar(235, 240, 245),
            1,
            OpenCvSharp.LineTypes.AntiAlias);

        // Draw top 2 rows: 5 Good Train + 5 BackBad Train
        for (var i = 0; i < 5; i++)
        {
            BlitCellOntoSheet(sheetMat, trainImages[i], cellW, cellH, in trainPoses[i], roiSize, 20 + (i * 176), 52, $"Train Good #{i + 1}", new OpenCvSharp.Scalar(80, 220, 100));
            BlitCellOntoSheet(sheetMat, trainImages[5 + i], cellW, cellH, in trainPoses[5 + i], roiSize, 20 + (i * 176), 210, $"Train BackBad #{i + 1}", new OpenCvSharp.Scalar(70, 165, 255));
        }

        // Evaluate each test PNG from disk and draw onto bottom row
        for (var t = 0; t < testSpecs.Length; t++)
        {
            var spec = testSpecs[t];
            var pngPath = Path.Combine(outputDir, spec.FileName);
            Assert.True(File.Exists(pngPath));

            using var loadedMat = OpenCvSharp.Cv2.ImRead(pngPath, OpenCvSharp.ImreadModes.Grayscale);
            Assert.Equal(cellW, loadedMat.Cols);
            Assert.Equal(cellH, loadedMat.Rows);

            var loadedBytes = new byte[cellW * cellH];
            System.Runtime.InteropServices.Marshal.Copy(loadedMat.Data, loadedBytes, 0, loadedBytes.Length);

            var pred = classifier.Predict(loadedBytes, cellW, cellH, cellW, in spec.Pose, model, workspace);
            Console.WriteLine($"[PNG Test] {spec.FileName} -> Pred={pred.PredictedClass.Name} (OOD={pred.IsOutOfDistribution}, d_M={pred.MahalanobisDistance:F2} / Thr={model.OodMahalanobisThreshold:F2}, Conf={pred.Confidence:P1})");

            Assert.Equal(spec.ExpectOod, pred.IsOutOfDistribution);
            Assert.Equal(spec.ExpectedClassValue, pred.PredictedClass.Value);

            var boxColor = pred.IsOutOfDistribution
                ? new OpenCvSharp.Scalar(70, 70, 255)
                : (pred.PredictedClass.Value == goodClass.Value
                    ? new OpenCvSharp.Scalar(80, 230, 100)
                    : new OpenCvSharp.Scalar(0, 180, 255));

            var label = $"{pred.PredictedClass.Name} (dM={pred.MahalanobisDistance:F1})";
            BlitCellOntoSheet(sheetMat, loadedBytes, cellW, cellH, in spec.Pose, roiSize, 20 + (t * 220), 372, label, boxColor);
        }

        var sheetPath = Path.Combine(outputDir, "edge-fewshot-test-sheet.png");
        Assert.True(OpenCvSharp.Cv2.ImWrite(sheetPath, sheetMat));
        Assert.True(File.Exists(sheetPath));

        // 6. Generate a single-image multi-component PCB panel (edge-fewshot-single-board.png)
        // where 14 components (5 Train Good, 5 Train BackBad, 4 Test Targets) coexist on one clean grayscale board.
        const int boardW = 1000;
        const int boardH = 560;
        var singleBoardPixels = new byte[boardW * boardH];
        for (var y = 0; y < boardH; y++)
        {
            var rowOffset = y * boardW;
            for (var x = 0; x < boardW; x++)
            {
                var bg = 34.0f + (MathF.Sin(x * 0.25f) * MathF.Cos(y * 0.25f) * 3.0f);
                singleBoardPixels[rowOffset + x] = (byte)MathF.Round(bg);
            }
        }

        var singleBoardTrainPoses = new RoiFixturePose[10];
        var singleBoardTrainImages = new List<byte[]>(10);

        for (var i = 0; i < 5; i++)
        {
            var gx = 110.0f + (i * 190.0f) + ((i - 2) * 1.5f);
            var gy = 105.0f + ((i - 2) * 1.0f);
            var pose = RoiFixturePose.Create(gx, gy, (i - 2) * 0.045f, 1.0f);
            StampComponentOntoBoard(singleBoardPixels, boardW, boardH, in pose, SyntheticPartKind.Good, i % 2, 0.90f + (i * 0.05f), (i - 2) * 5.0f);
            singleBoardTrainPoses[i] = pose;
            singleBoardTrainImages.Add(singleBoardPixels);
        }

        for (var i = 0; i < 5; i++)
        {
            var gx = 110.0f + (i * 190.0f) - ((i - 2) * 1.2f);
            var gy = 275.0f + ((i - 2) * 1.1f);
            var pose = RoiFixturePose.Create(gx, gy, (2 - i) * 0.04f, 1.0f);
            StampComponentOntoBoard(singleBoardPixels, boardW, boardH, in pose, SyntheticPartKind.BackBadDrip, i % 2, 0.92f + (i * 0.04f), (i - 2) * 4.0f);
            singleBoardTrainPoses[5 + i] = pose;
            singleBoardTrainImages.Add(singleBoardPixels);
        }

        var singleBoardTestPoses = new RoiFixturePose[testSpecs.Length];
        for (var t = 0; t < testSpecs.Length; t++)
        {
            var spec = testSpecs[t];
            var gx = 160.0f + (t * 225.0f);
            var gy = 445.0f;
            var pose = RoiFixturePose.Create(gx, gy, spec.Pose.AngleRad, spec.Pose.Scale);
            StampComponentOntoBoard(singleBoardPixels, boardW, boardH, in pose, spec.Kind, spec.Variant, spec.Gain, spec.Bias);
            singleBoardTestPoses[t] = pose;
        }

        var singleBoardPath = Path.Combine(outputDir, "edge-fewshot-single-board.png");
        SaveGrayscalePng(singleBoardPath, singleBoardPixels, boardW, boardH);
        Assert.True(File.Exists(singleBoardPath));

        // Reload the single board PNG from disk, train on its top 10 component ROIs, and classify its bottom 4 component ROIs
        using var reloadedBoardMat = OpenCvSharp.Cv2.ImRead(singleBoardPath, OpenCvSharp.ImreadModes.Grayscale);
        var reloadedBoardBytes = new byte[boardW * boardH];
        System.Runtime.InteropServices.Marshal.Copy(reloadedBoardMat.Data, reloadedBoardBytes, 0, reloadedBoardBytes.Length);

        var singleBoardModel = trainer.TrainFromImages(
            singleBoardTrainImages,
            boardW,
            boardH,
            boardW,
            singleBoardTrainPoses,
            trainLabels,
            in options);

        for (var t = 0; t < testSpecs.Length; t++)
        {
            var pred = classifier.Predict(reloadedBoardBytes, boardW, boardH, boardW, in singleBoardTestPoses[t], singleBoardModel, workspace);
            Assert.Equal(testSpecs[t].ExpectOod, pred.IsOutOfDistribution);
            Assert.Equal(testSpecs[t].ExpectedClassValue, pred.PredictedClass.Value);
        }
    }

    private enum SyntheticPartKind
    {
        Good = 0,
        BackBadDrip = 1,
        UnknownSevereContamination = 2
    }

    private static void RenderSyntheticPartScene(
        Span<byte> image,
        int width,
        int height,
        int stride,
        in RoiFixturePose pose,
        SyntheticPartKind defectKind,
        int defectVariant,
        float gain,
        float bias)
    {
        image.Fill(30);
        var (sin, cos) = MathF.SinCos(-pose.AngleRad);
        var invScale = 1f / pose.Scale;

        for (var y = 0; y < height; y++)
        {
            var rowOffset = y * stride;
            var dy = (y + 0.5f) - pose.CenterY;
            for (var x = 0; x < width; x++)
            {
                var dx = (x + 0.5f) - pose.CenterX;
                var lx = ((dx * cos) - (dy * sin)) * invScale;
                var ly = ((dx * sin) + (dy * cos)) * invScale;

                // Canonical part body inside [-12, 12] x [-12, 12]
                var intensity = 40.0f;
                var radius = MathF.Sqrt((lx * lx) + (ly * ly));
                if (MathF.Abs(lx) <= 11.0f && MathF.Abs(ly) <= 11.0f)
                {
                    intensity = 155.0f + (lx * 0.8f);
                }

                // Inner circular cavity
                if (radius <= 5.5f)
                {
                    intensity = 75.0f;
                }

                if (defectKind == SyntheticPartKind.BackBadDrip)
                {
                    // Multi-modal vertical drip streak on either upper-right (variant 0) or lower-left (variant 1)
                    var dripYCenter = defectVariant == 0 ? -7.5f : 7.5f;
                    var dripXCenter = defectVariant == 0 ? 6.5f : -6.5f;
                    if (MathF.Abs(lx - dripXCenter) <= 2.2f && MathF.Abs(ly - dripYCenter) <= 5.0f)
                    {
                        intensity = 240.0f;
                    }
                }
                else if (defectKind == SyntheticPartKind.UnknownSevereContamination)
                {
                    // High-frequency diagonal cross-hatch splatter across the entire surface
                    var wave1 = MathF.Sin((lx + ly) * 1.8f);
                    var wave2 = MathF.Cos((lx - ly) * 1.8f);
                    intensity = 130.0f + (wave1 * wave2 * 115.0f);
                }

                var transformed = System.Math.Clamp((intensity * gain) + bias, 0f, 255f);
                image[rowOffset + x] = (byte)MathF.Round(transformed);
            }
        }
    }

    private static void RenderHighResolutionComponent(
        Span<byte> image,
        int width,
        int height,
        int stride,
        in RoiFixturePose pose,
        SyntheticPartKind defectKind,
        int defectVariant,
        float gain,
        float bias)
    {
        var (sin, cos) = MathF.SinCos(-pose.AngleRad);
        var invScale = 1f / pose.Scale;

        for (var y = 0; y < height; y++)
        {
            var rowOffset = y * stride;
            var dy = (y + 0.5f) - pose.CenterY;
            for (var x = 0; x < width; x++)
            {
                var dx = (x + 0.5f) - pose.CenterX;
                var lx = ((dx * cos) - (dy * sin)) * invScale;
                var ly = ((dx * sin) + (dy * cos)) * invScale;

                // Subtle PCB substrate background texture
                var bgWave = MathF.Sin(x * 0.25f) * MathF.Cos(y * 0.25f) * 3.0f;
                var intensity = 34.0f + bgWave;

                // Metallic terminal leads on left and right sides
                if (MathF.Abs(ly) <= 16.0f && MathF.Abs(lx) >= 21.0f && MathF.Abs(lx) <= 27.0f)
                {
                    var pinIndex = (int)MathF.Floor((ly + 16.0f) / 8.0f);
                    var pinCenterY = -12.0f + (pinIndex * 8.0f);
                    if (MathF.Abs(ly - pinCenterY) <= 2.2f)
                    {
                        intensity = 205.0f;
                    }
                }

                // Molded IC package body [-21, 21] x [-21, 21]
                if (MathF.Abs(lx) <= 21.0f && MathF.Abs(ly) <= 21.0f)
                {
                    intensity = 148.0f + (lx * 0.45f) - (ly * 0.35f);

                    // Bevel border
                    if (MathF.Abs(lx) >= 18.5f || MathF.Abs(ly) >= 18.5f)
                    {
                        intensity = 182.0f;
                    }
                }

                // Center circular cavity / fiducial recess
                var radius = MathF.Sqrt((lx * lx) + (ly * ly));
                if (radius <= 9.5f)
                {
                    intensity = 72.0f + (radius * 1.5f);
                }

                // Inner die pad
                if (radius <= 4.2f)
                {
                    intensity = 132.0f;
                }

                if (defectKind == SyntheticPartKind.BackBadDrip)
                {
                    // Multi-modal bright conductive adhesive/solder drip lobe
                    var dripX = defectVariant == 0 ? 12.0f : -12.0f;
                    var dripY = defectVariant == 0 ? -12.5f : 12.5f;
                    var distX = (lx - dripX) / 4.2f;
                    var distY = (ly - dripY) / 8.5f;
                    if ((distX * distX) + (distY * distY) <= 1.0f)
                    {
                        intensity = 246.0f;
                    }
                }
                else if (defectKind == SyntheticPartKind.UnknownSevereContamination)
                {
                    // Unseen high-frequency diagonal shatter / foreign splatter across the package
                    var wave1 = MathF.Sin((lx + ly) * 1.35f);
                    var wave2 = MathF.Cos((lx - ly) * 1.35f);
                    if (MathF.Abs(lx) <= 23.0f && MathF.Abs(ly) <= 23.0f)
                    {
                        intensity = 130.0f + (wave1 * wave2 * 118.0f);
                    }
                }

                var transformed = System.Math.Clamp((intensity * gain) + bias, 0f, 255f);
                image[rowOffset + x] = (byte)MathF.Round(transformed);
            }
        }
    }

    private static void StampComponentOntoBoard(
        Span<byte> boardImage,
        int boardWidth,
        int boardHeight,
        in RoiFixturePose pose,
        SyntheticPartKind defectKind,
        int defectVariant,
        float gain,
        float bias)
    {
        const int patchRadius = 56;
        var minX = System.Math.Max(0, (int)MathF.Floor(pose.CenterX - patchRadius));
        var maxX = System.Math.Min(boardWidth - 1, (int)MathF.Ceiling(pose.CenterX + patchRadius));
        var minY = System.Math.Max(0, (int)MathF.Floor(pose.CenterY - patchRadius));
        var maxY = System.Math.Min(boardHeight - 1, (int)MathF.Ceiling(pose.CenterY + patchRadius));

        var patchW = (maxX - minX) + 1;
        var patchH = (maxY - minY) + 1;
        var patchBuf = new byte[patchW * patchH];
        var localPose = RoiFixturePose.Create(pose.CenterX - minX, pose.CenterY - minY, pose.AngleRad, pose.Scale);

        RenderHighResolutionComponent(patchBuf, patchW, patchH, patchW, in localPose, defectKind, defectVariant, gain, bias);

        for (var py = 0; py < patchH; py++)
        {
            patchBuf.AsSpan(py * patchW, patchW).CopyTo(boardImage.Slice(((minY + py) * boardWidth) + minX, patchW));
        }
    }

    private static void SaveGrayscalePng(string filePath, byte[] pixels, int width, int height)
    {
        using var mat = new OpenCvSharp.Mat(height, width, OpenCvSharp.MatType.CV_8UC1);
        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, mat.Data, pixels.Length);
        OpenCvSharp.Cv2.ImWrite(filePath, mat);
    }

    private static void BlitCellOntoSheet(
        OpenCvSharp.Mat sheetMat,
        byte[] grayPixels,
        int cellW,
        int cellH,
        in RoiFixturePose pose,
        int roiSize,
        int offsetX,
        int offsetY,
        string caption,
        OpenCvSharp.Scalar accentColor)
    {
        using var grayMat = new OpenCvSharp.Mat(cellH, cellW, OpenCvSharp.MatType.CV_8UC1);
        System.Runtime.InteropServices.Marshal.Copy(grayPixels, 0, grayMat.Data, grayPixels.Length);
        using var colorCell = new OpenCvSharp.Mat();
        OpenCvSharp.Cv2.CvtColor(grayMat, colorCell, OpenCvSharp.ColorConversionCodes.GRAY2BGR);

        var half = roiSize * 0.5f;
        var c0 = pose.TransformLocalToSource(-half, -half);
        var c1 = pose.TransformLocalToSource(half, -half);
        var c2 = pose.TransformLocalToSource(half, half);
        var c3 = pose.TransformLocalToSource(-half, half);

        OpenCvSharp.Point[] pts =
        [
            new((int)MathF.Round(c0.SourceX), (int)MathF.Round(c0.SourceY)),
            new((int)MathF.Round(c1.SourceX), (int)MathF.Round(c1.SourceY)),
            new((int)MathF.Round(c2.SourceX), (int)MathF.Round(c2.SourceY)),
            new((int)MathF.Round(c3.SourceX), (int)MathF.Round(c3.SourceY))
        ];

        OpenCvSharp.Cv2.Polylines(colorCell, [pts], isClosed: true, accentColor, thickness: 2, OpenCvSharp.LineTypes.AntiAlias);

        using var roiView = new OpenCvSharp.Mat(sheetMat, new OpenCvSharp.Rect(offsetX, offsetY, cellW, cellH));
        colorCell.CopyTo(roiView);

        OpenCvSharp.Cv2.PutText(
            sheetMat,
            caption,
            new OpenCvSharp.Point(offsetX, offsetY + cellH + 15),
            OpenCvSharp.HersheyFonts.HersheySimplex,
            0.40,
            accentColor,
            1,
            OpenCvSharp.LineTypes.AntiAlias);
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "VisionAlgorithm")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return AppContext.BaseDirectory;
    }

    private static string? FindRepoOnnxModelPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "VisionAnomaly", "Models", "resnet18-patches.onnx");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }
}
