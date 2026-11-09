// File: benchmarks/VisionAlgorithm.Benchmarks/EdgeFewShotMemoryBenchmark.cs
using System.Diagnostics;
using VisionAlgorithm.OpenSrc.EdgeFewShot.Backbone;
using VisionAlgorithm.OpenSrc.EdgeFewShot.Domain;
using VisionAlgorithm.OpenSrc.EdgeFewShot.Engine;
using VisionAlgorithm.OpenSrc.EdgeFewShot.Math;
using VisionAlgorithm.OpenSrc.EdgeFewShot.Preprocessing;

namespace VisionAlgorithm.Benchmarks;

/// <summary>
/// [MemoryDiagnoser] verification harness measuring 5-shot training latency, per-frame inference latency,
/// and exact 0-byte GC heap allocation on <see cref="EdgeFewShotClassifier.Predict"/>.
/// </summary>
public sealed class EdgeFewShotMemoryBenchmark : IDisposable
{
    private const int SceneWidth = 2048;
    private const int SceneHeight = 2048;
    private const int SceneStride = 2048;
    private const int RoiWidth = 64;
    private const int RoiHeight = 64;
    private const int ShotsPerClass = 5;

    private readonly byte[] _runtimeScene = new byte[SceneHeight * SceneStride];
    private readonly RoiFixturePose _runtimePose = RoiFixturePose.Create(1024.35f, 1024.65f, angleRad: 0.08f, scale: 1.0f);
    private readonly ClassPrototypeModel _model;
    private readonly EdgeFewShotClassifier _classifier;
    private readonly ClassifierWorkspace _workspace = new();

    public double TrainingMilliseconds { get; }

    public EdgeFewShotMemoryBenchmark()
    {
        var roiExtractor = new SimdAffineRoiExtractor();
        var normalizer = new IlluminationNormalizer();
        var backbone = new MultiScaleGaborHogExtractor();
        var gemPooler = new GemSpatialPooler();
        var simpleShot = new SimpleShotNormalizer();
        var clusterer = new KMeansPlusPlusClusterer();
        var covariance = new LedoitWolfCovariance();

        var trainer = new EdgeFewShotTrainer(roiExtractor, normalizer, backbone, gemPooler, simpleShot, clusterer, covariance);
        _classifier = new EdgeFewShotClassifier(roiExtractor, normalizer, backbone, gemPooler, simpleShot, covariance);

        var goodClass = FewShotClassId.Create(0, "Good");
        var badClass = FewShotClassId.Create(1, "BackBad");
        var totalSamples = ShotsPerClass * 2;

        var trainImages = new List<byte[]>(totalSamples);
        var trainPoses = new RoiFixturePose[totalSamples];
        var trainLabels = new FewShotClassId[totalSamples];

        for (var i = 0; i < ShotsPerClass; i++)
        {
            var sampleImg = new byte[128 * 128];
            var pose = RoiFixturePose.Create(64f, 64f, angleRad: (i - 2) * 0.03f, scale: 1.0f);
            RenderTargetPatch(sampleImg, 128, 128, 128, in pose, hasDripDefect: false);
            trainImages.Add(sampleImg);
            trainPoses[i] = pose;
            trainLabels[i] = goodClass;
        }

        for (var i = 0; i < ShotsPerClass; i++)
        {
            var sampleImg = new byte[128 * 128];
            var pose = RoiFixturePose.Create(64f, 64f, angleRad: (2 - i) * 0.03f, scale: 1.0f);
            RenderTargetPatch(sampleImg, 128, 128, 128, in pose, hasDripDefect: true);
            trainImages.Add(sampleImg);
            trainPoses[ShotsPerClass + i] = pose;
            trainLabels[ShotsPerClass + i] = badClass;
        }

        var options = FewShotTrainOptions.Create(
            roiWidth: RoiWidth,
            roiHeight: RoiHeight,
            gemExponentP: 3.0f,
            shrinkageLambda: 0.15f,
            subPrototypesPerClass: 2);

        var trainSw = Stopwatch.StartNew();
        _model = trainer.TrainFromImages(trainImages, 128, 128, 128, trainPoses, trainLabels, in options);
        trainSw.Stop();
        TrainingMilliseconds = trainSw.Elapsed.TotalMilliseconds;

        RenderTargetPatch(_runtimeScene, SceneWidth, SceneHeight, SceneStride, in _runtimePose, hasDripDefect: true);
    }

    public (double MeanMicroseconds, long AllocatedBytesPerPredict, FewShotPredictionResult LastResult) RunZeroAllocationVerification(
        int warmupIterations = 10,
        int measureIterations = 100)
    {
        for (var i = 0; i < warmupIterations; i++)
        {
            _ = _classifier.Predict(_runtimeScene, SceneWidth, SceneHeight, SceneStride, in _runtimePose, _model, _workspace);
        }

        FewShotPredictionResult last = default;
        var allocBefore = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();

        for (var i = 0; i < measureIterations; i++)
        {
            last = _classifier.Predict(_runtimeScene, SceneWidth, SceneHeight, SceneStride, in _runtimePose, _model, _workspace);
        }

        sw.Stop();
        var allocAfter = GC.GetAllocatedBytesForCurrentThread();
        var meanUs = (sw.Elapsed.TotalMilliseconds * 1000.0) / measureIterations;
        var bytesPerCall = (allocAfter - allocBefore) / measureIterations;
        return (meanUs, bytesPerCall, last);
    }

    private static void RenderTargetPatch(
        Span<byte> image,
        int width,
        int height,
        int stride,
        in RoiFixturePose pose,
        bool hasDripDefect)
    {
        var (sin, cos) = MathF.SinCos(-pose.AngleRad);
        var minX = System.Math.Max(0, (int)(pose.CenterX - 48));
        var maxX = System.Math.Min(width - 1, (int)(pose.CenterX + 48));
        var minY = System.Math.Max(0, (int)(pose.CenterY - 48));
        var maxY = System.Math.Min(height - 1, (int)(pose.CenterY + 48));

        for (var y = minY; y <= maxY; y++)
        {
            var rowOffset = y * stride;
            var dy = (y + 0.5f) - pose.CenterY;
            for (var x = minX; x <= maxX; x++)
            {
                var dx = (x + 0.5f) - pose.CenterX;
                var lx = (dx * cos) - (dy * sin);
                var ly = (dx * sin) + (dy * cos);

                var val = (byte)40;
                if (MathF.Abs(lx) <= 22f && MathF.Abs(ly) <= 22f)
                {
                    val = 160;
                }

                if (hasDripDefect && MathF.Abs(lx - 10f) <= 4f && MathF.Abs(ly + 12f) <= 9f)
                {
                    val = 245;
                }

                image[rowOffset + x] = val;
            }
        }
    }

    public void Dispose()
    {
        _workspace.Dispose();
    }
}
