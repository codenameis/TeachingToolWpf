using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace VisionAnomaly.Learning;

internal sealed class PatchFeatureExtractor : IDisposable
{
    public const int Dimensions = 96;
    public const int Grid = 28;
    public const int PatchCount = Grid * Grid;
    private readonly InferenceSession _session;
    private readonly float[] _input = new float[3 * 224 * 224];
    private readonly byte[] _bytes = new byte[224 * 224 * 4];
    private static readonly float[] s_mean = [.485f, .456f, .406f];
    private static readonly float[] s_std = [.229f, .224f, .225f];

    public PatchFeatureExtractor(string modelPath)
    {
        using var options = new SessionOptions { IntraOpNumThreads = 2, InterOpNumThreads = 1 };
        _session = new(modelPath, options);
        if (!_session.InputMetadata.ContainsKey("image") || !_session.OutputMetadata.ContainsKey("patches"))
        {
            _session.Dispose();
            throw new InvalidDataException("지원하는 ResNet18 패치 특징 모델이 아닙니다.");
        }
    }

    public float[] Extract(Mat bgra)
    {
        Marshal.Copy(bgra.Data, _bytes, 0, _bytes.Length);
        for (var channel = 0; channel < 3; channel++)
        {
            for (var pixel = 0; pixel < 224 * 224; pixel++)
            {
                _input[(channel * 224 * 224) + pixel] = ((_bytes[(pixel * 4) + 2 - channel] / 255f) - s_mean[channel]) / s_std[channel];
            }
        }

        return Run(_input);
    }

    internal float[] Run(float[] input)
    {
        var tensor = new DenseTensor<float>(input, [1, 3, 224, 224]);
        using var result = _session.Run([NamedOnnxValue.CreateFromTensor("image", tensor)]);
        var output = result.First().AsTensor<float>().ToArray();
        if (output.Length != PatchCount * Dimensions || output.Any(value => !float.IsFinite(value)))
        {
            throw new InvalidDataException("특징 모델의 출력이 유효하지 않습니다.");
        }

        return output;
    }

    public void Dispose() => _session.Dispose();
}
