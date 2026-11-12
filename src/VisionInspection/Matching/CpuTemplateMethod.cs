using System.Diagnostics;
using CpuMatching.Core;
using CpuMatching.Line2D;
using CpuMatching.SubNcc;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Imaging.Matching;

public enum CpuTemplateMethod
{
    Line2D,
    SubNcc
}
