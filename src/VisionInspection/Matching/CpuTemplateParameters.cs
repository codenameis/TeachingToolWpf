using CpuMatching.DirectionalChamfer;
using CpuMatching.Line2D;
using CpuMatching.SubNcc;

namespace TeachingToolWpf.Imaging.Matching;

public sealed record CpuTemplateParameters
{
    public Line2DOptions Line2D { get; init; } = new();
    public SubNccOptions SubNcc { get; init; } = new();
    public DirectionalChamferOptions Chamfer { get; init; } = new();

    public void Validate(CpuTemplateMethod method)
    {
        switch (method)
        {
            case CpuTemplateMethod.Line2D:
                Line2D.Validate();
                break;
            case CpuTemplateMethod.SubNcc:
                SubNcc.Validate();
                break;
            case CpuTemplateMethod.DirectionalChamfer:
                Chamfer.Validate();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(method));
        }
    }
}
