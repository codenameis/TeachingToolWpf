// File: src/VisionAlgorithm/OpenSrc/EdgeFewShot/Backbone/OnnxFeatureMapExtractor.cs
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace VisionAlgorithm.OpenSrc.EdgeFewShot.Backbone;

/// <summary>
/// <see cref="Microsoft.ML.OnnxRuntime.InferenceSession"/> wrapper extracting intermediate truncated CNN/ViT
/// <c>Layer 2</c> and <c>Layer 3</c> feature maps (PaDiM ICPR 2021 / PatchCore CVPR 2022).
/// Supports replicating single-channel normalized grayscale ROIs across 3 RGB input channels when required by ImageNet backbones.
/// </summary>
public sealed class OnnxFeatureMapExtractor : IFeatureMapExtractor, IDisposable
{
    private const int DefaultRgbChannelCount = 3;

    private readonly InferenceSession _session;
    private readonly bool _ownsSession;
    private readonly string _inputNodeName;
    private readonly string _layer2OutputName;
    private readonly string _layer3OutputName;
    private readonly int _inputChannels;
    private readonly int _layer2Stride;
    private readonly int _layer3Stride;
    private float[] _inputBuffer = [];
    private bool _disposed;

    public int Layer2Channels { get; }
    public int Layer3Channels { get; }
    public int TotalConcatenatedChannels => Layer2Channels + Layer3Channels;

    public OnnxFeatureMapExtractor(
        string onnxModelPath,
        string inputNodeName,
        string layer2OutputName,
        string layer3OutputName,
        int layer2Channels,
        int layer3Channels,
        int inputChannels = DefaultRgbChannelCount,
        int layer2Stride = MultiScaleGaborHogExtractor.DefaultLayer2CellSize,
        int layer3Stride = MultiScaleGaborHogExtractor.DefaultLayer3CellSize,
        SessionOptions? sessionOptions = null)
        : this(
            CreateSession(onnxModelPath, sessionOptions),
            ownsSession: true,
            inputNodeName,
            layer2OutputName,
            layer3OutputName,
            layer2Channels,
            layer3Channels,
            inputChannels,
            layer2Stride,
            layer3Stride)
    {
    }

    public OnnxFeatureMapExtractor(
        InferenceSession session,
        bool ownsSession,
        string inputNodeName,
        string layer2OutputName,
        string layer3OutputName,
        int layer2Channels,
        int layer3Channels,
        int inputChannels = DefaultRgbChannelCount,
        int layer2Stride = MultiScaleGaborHogExtractor.DefaultLayer2CellSize,
        int layer3Stride = MultiScaleGaborHogExtractor.DefaultLayer3CellSize)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputNodeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(layer2OutputName);
        ArgumentException.ThrowIfNullOrWhiteSpace(layer3OutputName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(layer2Channels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(layer3Channels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(inputChannels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(layer2Stride);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(layer3Stride);

        _session = session;
        _ownsSession = ownsSession;
        _inputNodeName = inputNodeName;
        _layer2OutputName = layer2OutputName;
        _layer3OutputName = layer3OutputName;
        Layer2Channels = layer2Channels;
        Layer3Channels = layer3Channels;
        _inputChannels = inputChannels;
        _layer2Stride = layer2Stride;
        _layer3Stride = layer3Stride;
    }

    public (int GridWidth, int GridHeight) GetLayer2Dimensions(int roiWidth, int roiHeight)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(roiWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(roiHeight);
        return (System.Math.Max(1, roiWidth / _layer2Stride), System.Math.Max(1, roiHeight / _layer2Stride));
    }

    public (int GridWidth, int GridHeight) GetLayer3Dimensions(int roiWidth, int roiHeight)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(roiWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(roiHeight);
        return (System.Math.Max(1, roiWidth / _layer3Stride), System.Math.Max(1, roiHeight / _layer3Stride));
    }

    public int GetRequiredScratchFloats(int roiWidth, int roiHeight)
    {
        var (w2, h2) = GetLayer2Dimensions(roiWidth, roiHeight);
        return checked((_inputChannels * roiWidth * roiHeight) + (w2 * h2));
    }

    public void ExtractMidLayerMaps(
        ReadOnlySpan<float> normalizedRoi,
        int roiWidth,
        int roiHeight,
        Span<float> layer2Out,
        Span<float> layer3Out,
        Span<float> scratchBuffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(roiWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(roiHeight);

        var planePixels = checked(roiWidth * roiHeight);
        if (normalizedRoi.Length < planePixels)
        {
            throw new ArgumentException("normalizedRoi span is shorter than roiWidth * roiHeight.", nameof(normalizedRoi));
        }

        var inputFloats = checked(_inputChannels * planePixels);
        if (_inputBuffer.Length < inputFloats)
        {
            _inputBuffer = new float[inputFloats];
        }

        for (var c = 0; c < _inputChannels; c++)
        {
            normalizedRoi.Slice(0, planePixels).CopyTo(_inputBuffer.AsSpan(c * planePixels, planePixels));
        }

        var inputTensor = new DenseTensor<float>(_inputBuffer.AsMemory(0, inputFloats), [1, _inputChannels, roiHeight, roiWidth]);
        var inputs = new List<NamedOnnxValue>(1)
        {
            NamedOnnxValue.CreateFromTensor(_inputNodeName, inputTensor)
        };

        using var results = _session.Run(inputs, [_layer2OutputName, _layer3OutputName]);
        foreach (var item in results)
        {
            if (string.Equals(item.Name, _layer2OutputName, StringComparison.Ordinal))
            {
                CopyTensorToSpan(item.AsTensor<float>(), layer2Out);
            }
            else if (string.Equals(item.Name, _layer3OutputName, StringComparison.Ordinal))
            {
                CopyTensorToSpan(item.AsTensor<float>(), layer3Out);
            }
        }
    }

    private static void CopyTensorToSpan(Tensor<float> tensor, Span<float> destination)
    {
        if (tensor is DenseTensor<float> dense)
        {
            var srcSpan = dense.Buffer.Span;
            var count = System.Math.Min(srcSpan.Length, destination.Length);
            for (var i = 0; i < count; i++)
            {
                destination[i] = MathF.Max(0f, srcSpan[i]);
            }

            return;
        }

        var idx = 0;
        foreach (var val in tensor)
        {
            if (idx >= destination.Length)
            {
                break;
            }

            destination[idx++] = MathF.Max(0f, val);
        }
    }

    private static InferenceSession CreateSession(string onnxModelPath, SessionOptions? options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(onnxModelPath);
        return options is null
            ? new InferenceSession(onnxModelPath)
            : new InferenceSession(onnxModelPath, options);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_ownsSession)
        {
            _session.Dispose();
        }
    }
}
