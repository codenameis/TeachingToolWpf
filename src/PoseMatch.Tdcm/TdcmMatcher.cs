using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace PoseMatch.Tdcm;

public enum TdcmDevice { Cpu, Cuda }

/// <summary>공식 가중치의 추론 세션. 호출자는 진행 중 연산이 끝난 후 Dispose한다.</summary>
public sealed class TdcmMatcher : ITdcmMatcher
{
    private readonly InferenceSession _session;
    private bool _disposed;

    public TdcmMatcher(int threads = 4, TdcmDevice device = TdcmDevice.Cpu, string? profilePrefix = null)
    {
        if (threads is < 1 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(threads));
        }

        using var resource = typeof(TdcmMatcher).Assembly.GetManifestResourceStream("PoseMatch.Tdcm.Models.posematch-tdcm.onnx")
            ?? throw new InvalidOperationException("배포 모델이 없습니다.");
        using var bytes = new MemoryStream();
        resource.CopyTo(bytes);
        using var options = new SessionOptions { IntraOpNumThreads = threads, InterOpNumThreads = 1 };
        if (!Enum.IsDefined(device))
        {
            throw new ArgumentOutOfRangeException(nameof(device));
        }
        if (profilePrefix is not null)
        {
            options.ProfileOutputPathPrefix = profilePrefix;
            options.EnableProfiling = true;
        }
        if (device == TdcmDevice.Cuda)
        {
            using var cuda = new OrtCUDAProviderOptions();
            cuda.UpdateOptions(new Dictionary<string, string>
            {
                ["device_id"] = "0",
                ["cudnn_conv_algo_search"] = "HEURISTIC",
                ["use_tf32"] = "0",
                ["do_copy_in_default_stream"] = "1"
            });
            options.AppendExecutionProvider_CUDA(cuda);
        }
        _session = new InferenceSession(bytes.ToArray(), options);
    }

    public string EndProfiling()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _session.EndProfiling();
    }

    public Prediction Predict(PixelImage image, PixelImage template, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (image.Width is < 36 or > 1024 || image.Height is < 36 or > 1024 || image.Width % 4 != 0 || image.Height % 4 != 0)
        {
            throw new ArgumentException("추론 창은 각 변 36~1024의 4 배수여야 합니다. 큰 영상은 겹치는 창으로 처리하세요.", nameof(image));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var inputs = new[]
        {
            NamedOnnxValue.CreateFromTensor("origin", new DenseTensor<float>(image.Normalize(image.Width, image.Height), [1, 3, image.Height, image.Width])),
            NamedOnnxValue.CreateFromTensor("template", new DenseTensor<float>(template.Normalize(36, 36), [1, 3, 36, 36]))
        };
        using var run = new RunOptions();
        using var registration = cancellationToken.Register(() => run.Terminate = true);
        try
        {
            using var output = _session.Run(inputs, ["score", "sign", "cos", "scale_x", "scale_y"], run);
            cancellationToken.ThrowIfCancellationRequested();
            var maps = output.Select(value => value.AsTensor<float>().ToArray()).ToArray();
            return new(image.Width, image.Height, maps[0], maps[1], maps[2], maps[3], maps[4]);
        }
        catch (OnnxRuntimeException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _session.Dispose();
    }
}
