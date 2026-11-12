using System.Diagnostics;
using CpuMatching.Core;
using CpuMatching.Line2D;
using CpuMatching.SubNcc;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Imaging.Matching;

public sealed record CpuSearchOutput(SearchResult Result, double TotalMilliseconds, double ReadMilliseconds, double SearchMilliseconds)
{
    public long CompletedTimestamp
    {
        get; init;
    }
    public CoarseScoreDiagnostics? Acceleration
    {
        get; init;
    }
}
