using TorchSharp;
using static TorchSharp.torch;

namespace PoseMatch.Tdcm.TorchSharp;

public sealed class TorchTdcmMatcher : ITdcmMatcher
{
    private readonly TdcmNetwork _network;
    private readonly Device _device;
    private bool _disposed;

    public TorchTdcmMatcher(int threads = 4, TdcmDevice device = TdcmDevice.Cpu, string? checkpoint = null)
    {
        if (threads is < 1 or > 32 || !Enum.IsDefined(device))
        {
            throw new ArgumentOutOfRangeException(nameof(threads));
        }

        if (device == TdcmDevice.Cuda && !cuda.is_available())
        {
            throw new InvalidOperationException("TorchSharp CUDA 장치를 사용할 수 없습니다.");
        }
        // LibTorch 스레드 수는 프로세스 설정이다. 학습 CLI는 검사 앱과 별도 프로세스로 실행한다.
        set_num_threads(threads);
        backends.cuda.matmul.allow_tf32 = false;
        backends.cudnn.allow_tf32 = false;
        _device = device == TdcmDevice.Cuda ? CUDA : CPU;
        _network = new TdcmNetwork(_device, checkpoint);
        _network.eval();
    }

    public Prediction Predict(PixelImage image, PixelImage template, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateImage(image);
        cancellationToken.ThrowIfCancellationRequested();
        using var scope = NewDisposeScope();
        using var inference = no_grad();
        var maps = _network.forward(Input(image, _device), Template(template, _device));
        // cpu 복사는 CUDA 실행 완료까지 포함한다. 호출이 끝나기 전에 버퍼를 해제하지 않는다.
        var values = maps.Select(map => map.cpu().contiguous().data<float>().ToArray()).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        return new(image.Width, image.Height, values[0], values[1], values[2], values[3], values[4]);
    }

    public MatchOutput Find(PixelImage image, PixelImage template, MatchOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new();
        options.Validate();
        var result = PoseDecoder.Decode(Predict(image, template, cancellationToken), options, cancellationToken);
        return options.RefinePose ? result with
        {
            Matches = result.Matches.Select(match => PoseRefiner.Refine(image, template, match, cancellationToken)).ToArray()
        } : result;
    }

    internal static void ValidateImage(PixelImage image)
    {
        if (image.Width is < 36 or > 1024 || image.Height is < 36 or > 1024 || image.Width % 4 != 0 || image.Height % 4 != 0)
        {
            throw new ArgumentException("추론 창은 각 변36~1024의4 배수여야 합니다.", nameof(image));
        }
    }
    internal static Tensor Input(PixelImage image, Device device)
        => tensor(image.Normalize(image.Width, image.Height), [1L, 3, image.Height, image.Width], device: device);
    internal static Tensor Template(PixelImage image, Device device)
        => tensor(image.Normalize(36, 36), [1L, 3, 36, 36], device: device);

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
