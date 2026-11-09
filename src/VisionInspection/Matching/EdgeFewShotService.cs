using System.Collections.Immutable;
using System.Diagnostics;
using VisionAlgorithm.OpenSrc.EdgeFewShot.Backbone;
using VisionAlgorithm.OpenSrc.EdgeFewShot.Domain;
using VisionAlgorithm.OpenSrc.EdgeFewShot.Engine;
using VisionAlgorithm.OpenSrc.EdgeFewShot.Math;
using VisionAlgorithm.OpenSrc.EdgeFewShot.Preprocessing;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Imaging.Matching;

public sealed record EdgeFewShotSampleInput(Guid RoiId, string Name, RoiFixturePose Pose, FewShotClassId ClassId);

public sealed record EdgeFewShotTargetInput(Guid RoiId, string Name, RoiFixturePose Pose, ImmutableArray<VisionGeometry.PointD> Outline);

public sealed record EdgeFewShotItemOutput(
    Guid RoiId,
    string Name,
    RoiFixturePose Pose,
    ImmutableArray<VisionGeometry.PointD> Outline,
    FewShotPredictionResult Prediction,
    double Microseconds);

public sealed record EdgeFewShotTrainingOutput(
    ClassPrototypeModel Model,
    int SampleCount,
    int GoodCount,
    int DefectCount,
    double Milliseconds);

public sealed record EdgeFewShotClassificationOutput(
    ImmutableArray<EdgeFewShotItemOutput> Items,
    double TotalMilliseconds,
    double MeanMicroseconds);

public interface IEdgeFewShotService
{
    Task<EdgeFewShotTrainingOutput> TrainAsync(
        ImageReference image,
        bool live,
        IReadOnlyList<EdgeFewShotSampleInput> samples,
        FewShotTrainOptions options,
        CancellationToken token);

    Task<EdgeFewShotClassificationOutput> ClassifyAsync(
        ImageReference image,
        bool live,
        ClassPrototypeModel model,
        IReadOnlyList<EdgeFewShotTargetInput> targets,
        CancellationToken token);
}

/// <summary>
/// 원본 영상에서 각 ROI 주변 로컬 창만 읽어 소량 샘플 학습(EdgeFewShotTrainer)과
/// Zero-Allocation 마할라노비스 추론(EdgeFewShotClassifier)을 수행한다.
/// </summary>
public sealed class EdgeFewShotService(IImageLoader loader, Func<VideoFrame?> retainFrame) : IEdgeFewShotService
{
    private readonly SimdAffineRoiExtractor _roiExtractor = new();
    private readonly IlluminationNormalizer _illuminationNormalizer = new();
    private readonly MultiScaleGaborHogExtractor _featureExtractor = new();
    private readonly GemSpatialPooler _gemPooler = new();
    private readonly SimpleShotNormalizer _simpleShotNormalizer = new();
    private readonly KMeansPlusPlusClusterer _clusterer = new();
    private readonly LedoitWolfCovariance _covarianceEstimator = new();

    public Task<EdgeFewShotTrainingOutput> TrainAsync(
        ImageReference image,
        bool live,
        IReadOnlyList<EdgeFewShotSampleInput> samples,
        FewShotTrainOptions options,
        CancellationToken token)
        => Task.Run(async () =>
        {
            ArgumentNullException.ThrowIfNull(samples);
            options.EnsureInitialized();
            if (samples.Count < 2)
            {
                throw new ArgumentException("최소 2개 이상의 ROI 샘플을 등록해 주세요.");
            }

            using var reader = await MatchingImageReader.OpenAsync(image, live, loader, retainFrame, token).ConfigureAwait(false);
            reader.PreferDirectGray = true;

            var trainer = new EdgeFewShotTrainer(
                _roiExtractor,
                _illuminationNormalizer,
                _featureExtractor,
                _gemPooler,
                _simpleShotNormalizer,
                _clusterer,
                _covarianceEstimator);

            var timer = Stopwatch.StartNew();
            var trainingSamples = new RoiTrainingSample[samples.Count];

            for (int i = 0; i < samples.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var sample = samples[i];
                var (crop, localPose) = ReadLocalWindow(
                    reader,
                    image.Width,
                    image.Height,
                    sample.Pose,
                    options.RoiWidth,
                    options.RoiHeight,
                    token);
                trainingSamples[i] = new RoiTrainingSample(
                    crop.Pixels,
                    crop.Width,
                    crop.Height,
                    crop.Stride,
                    localPose,
                    sample.ClassId);
            }

            var model = trainer.TrainFromSamples(trainingSamples, in options);
            timer.Stop();
            int goodCount = 0;
            int defectCount = 0;
            for (int i = 0; i < samples.Count; i++)
            {
                if (string.Equals(samples[i].ClassId.Name, "Good", StringComparison.OrdinalIgnoreCase))
                {
                    goodCount++;
                }
                else
                {
                    defectCount++;
                }
            }

            return new EdgeFewShotTrainingOutput(model, samples.Count, goodCount, defectCount, timer.Elapsed.TotalMilliseconds);
        }, token);

    public Task<EdgeFewShotClassificationOutput> ClassifyAsync(
        ImageReference image,
        bool live,
        ClassPrototypeModel model,
        IReadOnlyList<EdgeFewShotTargetInput> targets,
        CancellationToken token)
        => Task.Run(async () =>
        {
            ArgumentNullException.ThrowIfNull(model);
            ArgumentNullException.ThrowIfNull(targets);
            if (targets.Count == 0)
            {
                throw new ArgumentException("분류할 대상 ROI가 없습니다.");
            }

            using var reader = await MatchingImageReader.OpenAsync(image, live, loader, retainFrame, token).ConfigureAwait(false);
            reader.PreferDirectGray = true;

            var classifier = new EdgeFewShotClassifier(
                _roiExtractor,
                _illuminationNormalizer,
                _featureExtractor,
                _gemPooler,
                _simpleShotNormalizer,
                _covarianceEstimator);
            using var workspace = new ClassifierWorkspace();
            workspace.EnsureCapacity(model.RoiWidth, model.RoiHeight, _featureExtractor, model.TotalPrototypeCount, model.FeatureDimension);

            var results = ImmutableArray.CreateBuilder<EdgeFewShotItemOutput>(targets.Count);
            var totalTimer = Stopwatch.StartNew();
            long inferenceTicks = 0;

            bool canReadFull = (long)image.Width * image.Height <= VisionAlgorithm.OpenSrc.PatternMatching.PreparedSearchImage.MaximumPixels;
            VisionAlgorithm.GrayImage? fullImage = canReadFull ? reader.Read(0, 0, image.Width, image.Height, token) : null;

            for (int i = 0; i < targets.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var target = targets[i];
                var pose = target.Pose;

                FewShotPredictionResult prediction;
                long startTick;
                if (fullImage is { } full)
                {
                    startTick = Stopwatch.GetTimestamp();
                    prediction = classifier.Predict(
                        full.Pixels.Span,
                        full.Width,
                        full.Height,
                        full.Stride,
                        in pose,
                        model,
                        workspace);
                    inferenceTicks += Stopwatch.GetTimestamp() - startTick;
                }
                else
                {
                    var (crop, localPose) = ReadLocalWindow(reader, image.Width, image.Height, pose, model.RoiWidth, model.RoiHeight, token);
                    startTick = Stopwatch.GetTimestamp();
                    prediction = classifier.Predict(
                        crop.Pixels.Span,
                        crop.Width,
                        crop.Height,
                        crop.Stride,
                        in localPose,
                        model,
                        workspace);
                    inferenceTicks += Stopwatch.GetTimestamp() - startTick;
                }

                double itemUs = (Stopwatch.GetTimestamp() - startTick) * 1_000_000.0 / Stopwatch.Frequency;
                results.Add(new EdgeFewShotItemOutput(target.RoiId, target.Name, pose, target.Outline, prediction, itemUs));
            }

            totalTimer.Stop();
            double meanUs = targets.Count > 0
                ? (inferenceTicks * 1_000_000.0 / Stopwatch.Frequency) / targets.Count
                : 0.0;

            return new EdgeFewShotClassificationOutput(results.MoveToImmutable(), totalTimer.Elapsed.TotalMilliseconds, meanUs);
        }, token);

    private static (VisionAlgorithm.GrayImage Crop, RoiFixturePose LocalPose) ReadLocalWindow(
        MatchingImageReader reader,
        int imageWidth,
        int imageHeight,
        RoiFixturePose pose,
        int roiWidth,
        int roiHeight,
        CancellationToken token)
    {
        double scaledWidth = (double)roiWidth * pose.ScaleX;
        double scaledHeight = (double)roiHeight * pose.ScaleY;
        int radius = (int)System.Math.Ceiling(System.Math.Sqrt((scaledWidth * scaledWidth) + (scaledHeight * scaledHeight)) * 0.65) + 8;
        int left = System.Math.Clamp((int)System.Math.Floor(pose.CenterX) - radius, 0, System.Math.Max(0, imageWidth - 1));
        int top = System.Math.Clamp((int)System.Math.Floor(pose.CenterY) - radius, 0, System.Math.Max(0, imageHeight - 1));
        int right = System.Math.Clamp((int)System.Math.Ceiling(pose.CenterX) + radius, left + 1, imageWidth);
        int bottom = System.Math.Clamp((int)System.Math.Ceiling(pose.CenterY) + radius, top + 1, imageHeight);

        var crop = reader.Read(left, top, right - left, bottom - top, token);
        var localPose = RoiFixturePose.CreateAnisotropic(pose.CenterX - left, pose.CenterY - top, pose.AngleRad, pose.ScaleX, pose.ScaleY);
        return (crop, localPose);
    }
}
