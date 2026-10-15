using TorchSharp;
using static TorchSharp.torch;
using F = TorchSharp.torch.nn.functional;

namespace PoseMatch.Tdcm.TorchSharp;

/// <summary>공식 ConvNeXtV2 두 분기와 동적 depthwise 상관·다섯 pose head의 C# 구현.</summary>
internal sealed class TdcmNetwork : nn.Module<Tensor, Tensor, Tensor[]>
{
    private readonly Dictionary<string, Tensor> _weights = new();

    internal TdcmNetwork(Device device, string? checkpoint = null) : base("PoseMatchTDCM")
    {
        using var scope = NewDisposeScope();
        try
        {
            using var embedded = typeof(TdcmNetwork).Assembly.GetManifestResourceStream("PoseMatch.Tdcm.TorchSharp.Models.official.tdcm")
                ?? throw new InvalidOperationException("공식 가중치 리소스가 없습니다.");
            var entries = TdcmWeights.Read(embedded);
            if (!string.IsNullOrWhiteSpace(checkpoint))
            {
                using var input = File.OpenRead(checkpoint);
                var replacement = TdcmWeights.Read(input);
                if (entries.Count != replacement.Count || entries.Any(e => !replacement.Any(r => r.Name == e.Name && r.Shape.SequenceEqual(e.Shape))))
                {
                    throw new InvalidDataException("체크포인트의 모델 구조가 다릅니다.");
                }

                entries = replacement;
            }
            foreach (var item in entries)
            {
                var data = tensor(item.Values, item.Shape, device: device);
                var buffer = item.Name.EndsWith("running_mean", StringComparison.Ordinal) || item.Name.EndsWith("running_var", StringComparison.Ordinal);
                if (buffer)
                {
                    data.DetachFromDisposeScope();
                    register_buffer(item.Name, data);
                    _weights.Add(item.Name, data);
                }
                else
                {
                    var parameter = nn.Parameter(data);
                    parameter.DetachFromDisposeScope();
                    data.Dispose();
                    register_parameter(item.Name, parameter);
                    _weights.Add(item.Name, parameter);
                }
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public override Tensor[] forward(Tensor origin, Tensor template)
    {
        using var scope = NewDisposeScope();
        var scene = Backbone(origin, "backbone_O");
        var patch = Backbone(template, "backbone_T");
        var rows = new List<Tensor>();
        for (var b = 0; b < scene.shape[0]; b++)
        {
            var correlated = F.conv2d(scene[b].unsqueeze(0), patch[b].reshape(96, 1, 9, 9), padding: [4, 4], groups: 96);
            rows.Add(Conv(correlated, "pointwise_conv", 1));
        }
        var combined = cat(rows.ToArray(), 0);
        var up = F.pixel_shuffle(F.relu(BatchNorm(Conv(combined, "conv1", 1), "bn1")), 2);
        up = F.pixel_shuffle(F.relu(BatchNorm(Conv(up, "conv2", 1), "bn2")), 2);
        Tensor Head(string name) => Conv(F.relu(Conv(up, name + ".0", 1)), name + ".2", 0);
        Tensor[] result = [Head("conv_score").sigmoid(), Head("conv_sign").sigmoid(), Head("conv_cos").tanh(),
            (Head("conv_scale_x").sigmoid() * 1.5) + .5, (Head("conv_scale_y").sigmoid() * 1.5) + .5];
        foreach (var tensor in result)
        {
            tensor.MoveToOuterDisposeScope();
        }

        return result;
    }

    private Tensor Backbone(Tensor input, string name)
    {
        var x = Conv(input, name + ".stem.0", 0, 4).permute(0, 2, 3, 1);
        x = Norm(x, name + ".stem.1").permute(0, 3, 1, 2);
        for (var i = 0; i < 3; i++)
        {
            var prefix = name + $".stage1.blocks.{i}";
            var residual = x;
            x = Conv(x, prefix + ".conv_dw", 3, groups: 96).permute(0, 2, 3, 1);
            x = Norm(x, prefix + ".norm");
            x = F.gelu(Linear(x, prefix + ".mlp.fc1"));
            var magnitude = linalg.vector_norm(x, 2, [1, 2], keepdim: true);
            var normalized = magnitude / (magnitude.mean([-1L], keepdim: true) + 1e-6);
            x = x + addcmul(_weights[prefix + ".mlp.grn.bias"].reshape(1, 1, 1, 384),
                _weights[prefix + ".mlp.grn.weight"].reshape(1, 1, 1, 384), x * normalized, 1);
            x = Linear(x, prefix + ".mlp.fc2").permute(0, 3, 1, 2) + residual;
        }
        return BatchNorm(x, name + ".norm");
    }

    private Tensor Conv(Tensor input, string name, long padding, long stride = 1, long groups = 1)
        => F.conv2d(input, _weights[name + ".weight"], _weights[name + ".bias"], [stride, stride], [padding, padding], groups: groups);
    private Tensor Norm(Tensor input, string name)
        => F.layer_norm(input, [96], _weights[name + ".weight"], _weights[name + ".bias"], 1e-6);
    private Tensor Linear(Tensor input, string name)
        => F.linear(input, _weights[name + ".weight"], _weights[name + ".bias"]);
    // 작은 배치 미세학습에서는 공식 running 통계를 보존한다. affine 가중치의 gradient는 유지한다.
    private Tensor BatchNorm(Tensor input, string name)
        => F.batch_norm(input, _weights[name + ".running_mean"], _weights[name + ".running_var"],
            _weights[name + ".weight"], _weights[name + ".bias"], training: false);

    internal void Save(Stream stream) => TdcmWeights.Write(stream, _weights);
}
