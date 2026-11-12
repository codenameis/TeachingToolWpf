using System.Diagnostics;
using CpuMatching.Core;
using CpuMatching.Line2D;
using CpuMatching.SubNcc;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Imaging.Matching;

public sealed record CpuTemplate(CpuTemplateMethod Method, ITemplateMatcher Matcher, RoiGeometry TeachingRegion)
{
    public CpuTemplateParameters Parameters { get; init; } = new();
    public byte[]? PreviewGray8 { get; init; }
    public int PreviewWidth { get; init; }
    public int PreviewHeight { get; init; }
}
