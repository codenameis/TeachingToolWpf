namespace VisionAnomaly.Learning;

internal sealed class NormalTrainer
{
    private const int CandidateLimit = 8192;
    private readonly List<float[]> _candidates = [];
    private readonly List<float[]> _calibration = [];
    private readonly Random _random = new(17);
    private readonly double[] _motionMean = new double[FlowFeatures.Dimensions];
    private readonly double[] _motionM2 = new double[FlowFeatures.Dimensions];
    private int _seen;
    private int _motionSamples;
    public int Count
    {
        get; private set;
    }

    public void Add(float[] patches, float[]? motion)
    {
        if (Count >= 200)
        {
            throw new InvalidOperationException("정상 수집 예산200프레임에 도달했습니다. 모델을 완성하세요.");
        }
        Count++;
        if (Count % 5 == 0)
        {
            _calibration.Add(patches);
        }
        else
        {
            for (var patch = 0; patch < PatchFeatureExtractor.PatchCount; patch++)
            {
                var index = _seen < CandidateLimit ? _seen : _random.Next(_seen + 1);
                _seen++;
                if (index >= CandidateLimit)
                {
                    continue;
                }

                var vector = patches.AsSpan(patch * PatchFeatureExtractor.Dimensions, PatchFeatureExtractor.Dimensions).ToArray();
                if (index == _candidates.Count)
                {
                    _candidates.Add(vector);
                }
                else
                {
                    _candidates[index] = vector;
                }
            }
        }
        if (motion is null)
        {
            return;
        }

        _motionSamples++;
        for (var index = 0; index < motion.Length; index++)
        {
            var delta = motion[index] - _motionMean[index];
            _motionMean[index] += delta / _motionSamples;
            _motionM2[index] += delta * (motion[index] - _motionMean[index]);
        }
    }

    public NormalModel Fit(string hash, int width, int height, AnalysisRegion region, CancellationToken token)
    {
        if (Count < 30)
        {
            throw new InvalidOperationException("정상 프레임30개 이상이 필요합니다.");
        }

        var bank = PatchMemory.SelectCoreset(_candidates, 512, token);
        var maxima = _calibration.Select(patches => PatchMemory.Score(patches, bank, token).Max()).Order().ToArray();
        var threshold = Math.Max(.05f, maxima[^1] * 1.15f);
        return new(1, Guid.NewGuid(), hash, width, height, region, Count, bank, threshold,
            _motionMean.Select(value => (float)value).ToArray(),
            _motionM2.Select(value => (float)Math.Sqrt(value / Math.Max(1, _motionSamples - 1))).ToArray(),
            _motionSamples, DateTimeOffset.UtcNow);
    }
}
