using TorchSharp;
using static TorchSharp.torch;
using F = TorchSharp.torch.nn.functional;

namespace PoseMatch.Tdcm.TorchSharp;

public sealed record TdcmTrainingSample(PixelImage Image, PixelImage Template, IReadOnlyList<PoseMatchResult> Targets);
public sealed record TdcmTrainingOptions
{
    public int Epochs { get; init; } = 1;
    public double LearningRate { get; init; } = 1e-5;
    public double PositiveWeight { get; init; } = 1;
}
public sealed record TdcmTrainingProgress(int Step, int TotalSteps, double Loss);

/// <summary>원본 모델의 모든 parameter를 C# autograd로 미세학습한다. BN running 통계는 고정한다.</summary>
public sealed class TdcmTrainer : IDisposable
{
    private readonly TdcmNetwork _network;
    private readonly Device _device;
    private bool _disposed;

    public TdcmTrainer(TdcmDevice device = TdcmDevice.Cpu, int threads = 4, string? checkpoint = null)
    {
        if (!Enum.IsDefined(device) || threads is < 1 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(device));
        }

        if (device == TdcmDevice.Cuda && !cuda.is_available())
        {
            throw new InvalidOperationException("TorchSharp CUDA 장치를 사용할 수 없습니다.");
        }

        set_num_threads(threads);
        backends.cuda.matmul.allow_tf32 = false;
        backends.cudnn.allow_tf32 = false;
        _device = device == TdcmDevice.Cuda ? CUDA : CPU;
        _network = new TdcmNetwork(_device, checkpoint);
    }

    public void Train(IReadOnlyList<TdcmTrainingSample> samples, TdcmTrainingOptions options,
        Action<TdcmTrainingProgress>? progress = null, CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (samples.Count is < 1 or > 10000 || options.Epochs is < 1 or > 10000 ||
            !double.IsFinite(options.LearningRate) || options.LearningRate is <= 0 or > .01 ||
            !double.IsFinite(options.PositiveWeight) || options.PositiveWeight is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        foreach (var sample in samples)
        {
            Validate(sample);
        }

        var total = checked(samples.Count * options.Epochs);
        _network.train();
        using var optimizer = optim.Adam(_network.parameters(), lr: options.LearningRate);
        var step = 0;
        for (var epoch = 0; epoch < options.Epochs; epoch++)
        {
            foreach (var sample in samples)
            {
                token.ThrowIfCancellationRequested();
                using var scope = NewDisposeScope();
                optimizer.zero_grad();
                var output = _network.forward(TorchTdcmMatcher.Input(sample.Image, _device), TorchTdcmMatcher.Template(sample.Template, _device));
                var labels = Labels(sample);
                var truth = labels.Select(a => tensor(a, [1L, 1, sample.Image.Height, sample.Image.Width], device: _device)).ToArray();
                var mask = truth[0];
                var denominator = mask.sum().clamp_min(1);
                // 중심은 전체 창의 극히 일부다. 픽셀 전체 평균 BCE는 모두 배경으로
                // 예측해도 작아지므로 양성/음성 집합을 각각 정규화한다.
                var positive = mask.gt(.5).to_type(ScalarType.Float32);
                var negative = 1 - positive;
                var classification = F.binary_cross_entropy(output[0], positive, reduction: nn.Reduction.None);
                var loss = (options.PositiveWeight * (classification * positive).sum() / positive.sum().clamp_min(1))
                    + ((classification * negative).sum() / negative.sum().clamp_min(1));
                // 배경에는 방향/배율 정답이 없다. 가까운 중심의 pose만 감독한다.
                loss = loss + ((F.binary_cross_entropy(output[1], truth[1], reduction: nn.Reduction.None) * mask).sum() / denominator);
                for (var h = 2; h < 5; h++)
                {
                    loss = loss + (((output[h] - truth[h]).square() * mask).sum() / denominator);
                }

                var value = loss.item<float>();
                if (!double.IsFinite(value))
                {
                    throw new InvalidOperationException("학습 loss가 유한하지 않습니다. 가중치를 저장하지 않았습니다.");
                }

                loss.backward();
                var gradientNorm = nn.utils.clip_grad_norm_(_network.parameters(), 1);
                if (!double.IsFinite(gradientNorm))
                {
                    throw new InvalidOperationException("학습 gradient가 유한하지 않습니다. 갱신을 중지합니다.");
                }

                token.ThrowIfCancellationRequested();
                optimizer.step();
                progress?.Invoke(new(++step, total, value));
            }
        }
        _network.eval();
    }

    private static void Validate(TdcmTrainingSample sample)
    {
        TorchTdcmMatcher.ValidateImage(sample.Image);
        foreach (var target in sample.Targets)
        {
            if (!double.IsFinite(target.X) || !double.IsFinite(target.Y) || !double.IsFinite(target.AngleDegrees) ||
                target.X < 0 || target.X >= sample.Image.Width || target.Y < 0 || target.Y >= sample.Image.Height ||
                !double.IsFinite(target.Width) || !double.IsFinite(target.Height) || target.Width is < 18 or > 72 || target.Height is < 18 or > 72)
            {
                throw new ArgumentException("학습 정답의 중심/각도/크기(18~72px)를 확인하세요.");
            }
        }
    }

    private static float[][] Labels(TdcmTrainingSample sample)
    {
        var size = sample.Image.Width * sample.Image.Height;
        float[][] result = [new float[size], new float[size], new float[size], new float[size], new float[size]];
        foreach (var target in sample.Targets)
        {
            var angle = Math.IEEERemainder(target.AngleDegrees, 360);
            for (var y = Math.Max(0, (int)target.Y - 5); y <= Math.Min(sample.Image.Height - 1, (int)target.Y + 5); y++)
            {
                for (var x = Math.Max(0, (int)target.X - 5); x <= Math.Min(sample.Image.Width - 1, (int)target.X + 5); x++)
                {
                    var index = (y * sample.Image.Width) + x;
                    var weight = (float)Math.Exp(-(((x - target.X) * (x - target.X)) + ((y - target.Y) * (y - target.Y))) / 4.5);
                    if (weight <= result[0][index])
                    {
                        continue;
                    }

                    result[0][index] = weight;
                    result[1][index] = angle < 0 ? 1 : 0;
                    result[2][index] = (float)Math.Cos(angle * Math.PI / 180);
                    result[3][index] = (float)(target.Width / 36);
                    result[4][index] = (float)(target.Height / 36);
                }
            }
        }
        return result;
    }

    /// <summary>가중치만 저장한다. optimizer 상태를 포함한 학습 재개 파일은 아니다. 기존 파일을 덮어쓰지 않는다.</summary>
    public void SaveCheckpoint(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        _network.Save(stream);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _network.Dispose();
    }
}
