using System.Diagnostics;
using System.Security.Cryptography;
using VisionAnomaly.Imaging;
using VisionAnomaly.Learning;
using VisionImaging.Abstractions;

namespace VisionAnomaly;

/// <summary>한 분석 worker에서만 사용한다. 학습은 정상 메모리/통계 산출이며 백본은 고정한다.</summary>
public sealed class AnomalyProcessor : IDisposable
{
    private readonly PatchFeatureExtractor _features;
    private readonly FlowFeatures _flow = new();
    private readonly string _featureHash;
    private NormalTrainer? _trainer;
    private int _width, _height;
    private AnalysisRegion _region;

    public AnomalyProcessor(string featurePath)
    {
        _featureHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(featurePath)));
        _features = new(featurePath);
    }

    public NormalModel? Model
    {
        get; private set;
    }
    public int TrainingFrames => _trainer?.Count ?? Model?.TrainingFrames ?? 0;

    public void BeginLearning(int width, int height, AnalysisRegion region)
    {
        region.Validate();
        _width = width;
        _height = height;
        _region = region;
        _trainer = new();
        Model = null;
        _flow.Reset();
    }

    public void Learn(VideoFrame frame, CancellationToken token)
    {
        if (_trainer is null)
        {
            throw new InvalidOperationException("정상 수집을 먼저 시작하세요.");
        }

        CheckSize(frame);
        token.ThrowIfCancellationRequested();
        using var crop = FrameImages.Crop(frame, _region);
        var patches = _features.Extract(crop);
        var flow = _flow.Extract(crop, frame.Timestamp / (double)Stopwatch.Frequency);
        token.ThrowIfCancellationRequested();
        _trainer.Add(patches, flow);
    }

    public NormalModel CompleteLearning(CancellationToken token)
    {
        var model = (_trainer ?? throw new InvalidOperationException("수집 중이 아닙니다."))
            .Fit(_featureHash, _width, _height, _region, token);
        token.ThrowIfCancellationRequested();
        model.Validate();
        Model = model;
        _trainer = null;
        _flow.Reset();
        return model;
    }

    public void UseModel(NormalModel model)
    {
        model.Validate();
        if (!string.Equals(_featureHash, model.FeatureHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("정상 모델을 만든 특징 추출기와 현재 파일이 다릅니다.");
        }

        Model = model;
        _width = model.Width;
        _height = model.Height;
        _region = model.Region;
        _trainer = null;
        _flow.Reset();
    }

    public void ResetTemporal() => _flow.Reset();

    public AnomalyPrediction Predict(VideoFrame frame, CancellationToken token)
    {
        var model = Model ?? throw new InvalidOperationException("정상 기준을 먼저 학습하거나 불러오세요.");
        CheckSize(frame);
        var watch = Stopwatch.StartNew();
        using var crop = FrameImages.Crop(frame, _region);
        var scores = PatchMemory.Score(_features.Extract(crop), model.Memory, token);
        var flow = _flow.Extract(crop, frame.Timestamp / (double)Stopwatch.Frequency);
        var motion = 0f;
        var motionCells = new float[FlowFeatures.Grid * FlowFeatures.Grid];
        if (flow is not null && model.MotionSamples >= 10)
        {
            for (var index = 0; index < flow.Length; index++)
            {
                var score = Math.Abs(flow[index] - model.MotionMean[index]) / Math.Max(2, model.MotionDeviation[index]) / 4;
                motion = Math.Max(motion, score);
                motionCells[index / 3] = Math.Max(motionCells[index / 3], score);
            }
        }
        var bounds = FrameImages.Bounds(_width, _height, _region);
        var areas = scores.Select((score, index) => (score, index)).Where(item => item.score >= model.AppearanceThreshold * .5f)
            .OrderByDescending(item => item.score).Take(12).Select(item =>
            {
                var x = item.index % 28;
                var y = item.index / 28;
                return new AnomalyArea(bounds.X + (x / 28.0 * bounds.Width),
                    bounds.Y + (y / 28.0 * bounds.Height),
                    bounds.Width / 28.0, bounds.Height / 28.0, item.score / model.AppearanceThreshold);
            }).ToArray();
        var motionAreas = motionCells.Select((score, index) => (score, index)).Where(item => item.score >= .5f)
            .Select(item => new AnomalyArea(bounds.X + (item.index % 8 / 8.0 * bounds.Width),
                bounds.Y + (item.index / 8 / 8.0 * bounds.Height),
                bounds.Width / 8.0, bounds.Height / 8.0, item.score)).ToArray();
        return new(scores.Max() / model.AppearanceThreshold, motion, flow is not null && model.MotionSamples >= 10, areas, watch.Elapsed.TotalMilliseconds)
        {
            MotionAreas = motionAreas
        };
    }

    private void CheckSize(VideoFrame frame)
    {
        if (frame.Pixels.Width != _width || frame.Pixels.Height != _height)
        {
            throw new InvalidDataException("정상 학습과 영상 크기가 다릅니다. 현재 영상으로 다시 학습하세요.");
        }
    }

    public void Dispose()
    {
        _flow.Dispose();
        _features.Dispose();
    }
}
